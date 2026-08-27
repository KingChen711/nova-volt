using System.Globalization;
using MassTransit;
using Nvm.Contracts.CloudEvents;

namespace Nvm.Bus.CloudEvents;

/// <summary>The CloudEvents attributes read back off a received message.</summary>
/// <param name="SpecVersion">Specification version the publisher used.</param>
/// <param name="Id">Event identity — the value deduplication keys on.</param>
/// <param name="Type">What happened, and which schema version says so.</param>
/// <param name="Source">Which deployable, at which plant, asserted it.</param>
/// <param name="Time">When the publisher recorded the fact.</param>
/// <param name="DataContentType">
/// How the payload is encoded, for example <c>application/json</c>.
/// </param>
/// <remarks>
/// All six are mandatory and are read as one set, because the message this type is most useful for is
/// the one nobody can deserialize — sitting in an <c>_error</c> queue while somebody works out what it
/// was. Reporting the other five and leaving the encoding out invites the reader to assume JSON, which
/// is the assumption that put the message there in the first place.
/// </remarks>
public sealed record CloudEventAttributes(
    string SpecVersion,
    Guid Id,
    EventTypeName Type,
    EventSource Source,
    DateTimeOffset Time,
    string DataContentType);

/// <summary>Reads CloudEvents attributes off a consumed message.</summary>
/// <remarks>
/// <para>
/// An extension over the consume context rather than a filter feeding a scoped service. A filter
/// would have to run for every message whether anyone looked at the attributes or not, and would add
/// a registration that has to stay in step with the send side. Reading on demand does the same job
/// with nothing to keep in sync.
/// </para>
/// <para>
/// No validation on the way in either. A message that reached a consumer has already been routed and
/// deserialized by MassTransit, which used its own envelope to do it; refusing it here for a missing
/// <c>ce_</c> header would reject a message the system had already understood. That check belongs at
/// the edge where messages from outside this system arrive, and there is no such edge yet.
/// </para>
/// </remarks>
public static class CloudEventContextExtensions
{
    /// <summary>Reads the CloudEvents attributes, or null when the message carries none.</summary>
    /// <param name="context">The consume context.</param>
    public static CloudEventAttributes? CloudEvent(this ConsumeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var specVersion = Read(context, CloudEventHeaders.SpecVersion);

        if (specVersion is null)
        {
            return null;
        }

        return new CloudEventAttributes(
            specVersion,
            Guid.Parse(Require(context, CloudEventHeaders.Id), CultureInfo.InvariantCulture),
            EventTypeName.Parse(Require(context, CloudEventHeaders.Type)),
            EventSource.Parse(Require(context, CloudEventHeaders.Source)),
            DateTimeOffset.Parse(Require(context, CloudEventHeaders.Time), CultureInfo.InvariantCulture),
            Require(context, CloudEventHeaders.DataContentType));
    }

    private static string? Read(ConsumeContext context, string header) =>
        context.Headers.TryGetHeader(header, out var value) ? value as string : null;

    // Half a set of attributes is worse than none: a reader would take the ones present and silently
    // assume defaults for the rest. Either the publisher stamped the message or it did not.
    private static string Require(ConsumeContext context, string header) =>
        Read(context, header)
        ?? throw new InvalidOperationException(
            $"Message carries '{CloudEventHeaders.SpecVersion}' but not '{header}'. "
            + "CloudEvents attributes are written as a set.");
}
