using System.Globalization;
using MassTransit;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;

namespace Nvm.Bus.CloudEvents;

/// <summary>Stamps every outgoing event with its CloudEvents attributes as transport headers.</summary>
/// <typeparam name="TEvent">The event being sent.</typeparam>
/// <param name="applicationName">
/// Which deployable is publishing, in kebab-case — the second half of the source URN.
/// </param>
/// <remarks>
/// <para>
/// Two envelopes meet on this message and both are correct. MassTransit wraps the payload in its own
/// so that it can route, retry and fault; that envelope belongs to the library and may be replaced.
/// The CloudEvents attributes are the <b>business</b> envelope — what happened, where, when, who says
/// so — and that one may not. Keeping them as headers lets both exist without either pretending to be
/// the other. See <c>ADR-008</c>.
/// </para>
/// <para>
/// Headers rather than the body because the body is MassTransit's. A reader outside .NET — an
/// operator with <c>rabbitmqadmin</c>, a bridge to another system, a message sitting in an error
/// queue that no code could deserialize — can still see what the message claims to be.
/// </para>
/// </remarks>
internal sealed class CloudEventsSendFilter<TEvent>(string applicationName)
    : IFilter<SendContext<TEvent>>, IFilter<PublishContext<TEvent>>
    where TEvent : class, IDomainEvent
{
    private static readonly EventTypeName TypeName = EventTypeName.Of(typeof(TEvent));

    private readonly string _applicationName = applicationName;

    /// <inheritdoc />
    public Task Send(SendContext<TEvent> context, IPipe<SendContext<TEvent>> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Stamp(context);

        return next.Send(context);
    }

    /// <inheritdoc />
    public Task Send(PublishContext<TEvent> context, IPipe<PublishContext<TEvent>> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Stamp(context);

        return next.Send(context);
    }

    /// <inheritdoc />
    public void Probe(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.CreateFilterScope("nvm-cloudevents");
    }

    // Publish and send are separate pipes in MassTransit, and a filter on one does not run on the
    // other. Everything this system emits goes out through Publish, so installing on the send pipe
    // alone would have stamped nothing at all — and the headers would simply have been absent, with
    // no error anywhere to say so.
    private void Stamp(SendContext<TEvent> context)
    {
        var message = context.Message;

        context.Headers.Set(CloudEventHeaders.SpecVersion, CloudEventEnvelope<TEvent>.SpecVersionValue);

        // Straight off the payload, not generated here. This value is what deduplication at ingestion
        // and deduplication in the command pipeline both key on; a second source for it would be a
        // second thing to drift.
        context.Headers.Set(CloudEventHeaders.Id, message.EventId.ToString());

        context.Headers.Set(CloudEventHeaders.Type, TypeName.Value);
        context.Headers.Set(CloudEventHeaders.Source, EventSource.Create(message.SiteId, _applicationName).Value);

        // Round-trip format: unambiguous and lossless. RFC 3339 allows a trimmed fraction too, which
        // is what System.Text.Json writes in the event store's JSON — the same instant, spelled two
        // ways. Noted in ADR-008 so nobody reads it as a discrepancy.
        context.Headers.Set(CloudEventHeaders.Time, message.OccurredAt.ToString("O", CultureInfo.InvariantCulture));

        context.Headers.Set(CloudEventHeaders.DataContentType, CloudEventEnvelope<TEvent>.JsonContentType);
    }
}
