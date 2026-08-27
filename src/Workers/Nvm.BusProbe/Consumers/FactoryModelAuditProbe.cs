using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.BusProbe.Consumers;

/// <summary>Stands in for the service that writes plant changes into the audit trail.</summary>
/// <param name="logger">Where the one line of evidence per message goes.</param>
/// <remarks>
/// <para>
/// Second reader of the same fact, with a different reason for wanting it: IATF 16949 asks who
/// changed the plant model and when, and that record has to exist whether or not any cache was
/// listening. Two unrelated needs met by one publish is what "bus-centric" buys — the publisher knows
/// about neither, and adding a third reader in M2 changes nothing here.
/// </para>
/// <para>
/// Written as a separate consumer rather than a second line inside the cache probe on purpose. A
/// single consumer logging twice would produce the same two lines and prove nothing: the claim under
/// test is that two <b>queues</b> exist and fill independently.
/// </para>
/// </remarks>
[BusEndpoint("factory-model", "audit-trail")]
public sealed partial class FactoryModelAuditProbe(ILogger<FactoryModelAuditProbe> logger)
    : IConsumer<FactoryModelRevisionActivated>
{
    private readonly ILogger<FactoryModelAuditProbe> _logger = logger;

    /// <inheritdoc />
    public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (eventId, occurredAt, siteId, revision, _, _, _) = context.Message;

        Recorded(revision, siteId, occurredAt, eventId);

        return Task.CompletedTask;
    }

    // See FactoryModelCacheProbe for why these lines are source-generated.
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "audit-trail recorded revision {Revision} at {SiteId}, in force from {OccurredAt} "
            + "(ce_id {EventId})")]
    private partial void Recorded(int revision, string siteId, DateTimeOffset occurredAt, Guid eventId);
}
