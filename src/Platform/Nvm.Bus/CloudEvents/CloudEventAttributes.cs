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
/// A message carrying <b>no</b> <c>ce_</c> header at all is not an error. Not everything on a bus
/// comes from this system's publish path, and MassTransit routed and deserialized this one using its
/// own envelope; refusing it here would reject a message the system had already understood. Absent
/// attributes are a fact about the message, so they are reported as <see langword="null"/>.
/// </para>
/// <para>
/// A <b>partial</b> set is a different thing, and it is refused. The six attributes are written
/// together by one filter, so anything between one and five of them means the message was stamped by
/// something that does not agree with this system about what the set is. Reporting the ones present
/// invites the reader to default the rest — and the default for the missing encoding is exactly the
/// JSON assumption this envelope exists to prevent.
/// </para>
/// </remarks>
public static class CloudEventContextExtensions
{
    private const int MandatoryHeaderCount = 6;

    /// <summary>Reads the CloudEvents attributes, or null when the message carries none.</summary>
    /// <param name="context">The consume context.</param>
    /// <returns>
    /// The six attributes when all six headers are present; <see langword="null"/> when none of them
    /// is.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The message carries some of the mandatory headers but not all of them.
    /// </exception>
    public static CloudEventAttributes? CloudEvent(this ConsumeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // All six are read before any of them is judged. Picking one as a sentinel — specversion is
        // the tempting choice — makes that one header decide between "no attributes" and "a broken
        // set", so a message missing only the sentinel reads as a message carrying nothing at all.
        var specVersion = Read(context, CloudEventHeaders.SpecVersion);
        var id = Read(context, CloudEventHeaders.Id);
        var type = Read(context, CloudEventHeaders.Type);
        var source = Read(context, CloudEventHeaders.Source);
        var time = Read(context, CloudEventHeaders.Time);
        var dataContentType = Read(context, CloudEventHeaders.DataContentType);

        if (specVersion is null
            && id is null
            && type is null
            && source is null
            && time is null
            && dataContentType is null)
        {
            return null;
        }

        if (specVersion is null
            || id is null
            || type is null
            || source is null
            || time is null
            || dataContentType is null)
        {
            throw PartialSet(
                (CloudEventHeaders.SpecVersion, specVersion),
                (CloudEventHeaders.Id, id),
                (CloudEventHeaders.Type, type),
                (CloudEventHeaders.Source, source),
                (CloudEventHeaders.Time, time),
                (CloudEventHeaders.DataContentType, dataContentType));
        }

        return new CloudEventAttributes(
            specVersion,
            Guid.Parse(id, CultureInfo.InvariantCulture),
            EventTypeName.Parse(type),
            EventSource.Parse(source),
            DateTimeOffset.Parse(time, CultureInfo.InvariantCulture),
            dataContentType);
    }

    private static string? Read(ConsumeContext context, string header) =>
        context.Headers.TryGetHeader(header, out var value) ? value as string : null;

    // Half a set of attributes is worse than none: a reader would take the ones present and silently
    // assume defaults for the rest. Either the publisher stamped the message or it did not.
    //
    // The message names every header that is missing, not just the first one found. Someone reading
    // this off a message in an _error queue is trying to work out which publisher produced it, and
    // "three of the six are absent" narrows that down in a way "the first one is absent" does not.
    private static InvalidOperationException PartialSet(
        params (string Header, string? Value)[] attributes)
    {
        var missing = attributes.Where(pair => pair.Value is null).Select(pair => pair.Header);

        return new InvalidOperationException(
            $"Message carries only {attributes.Count(pair => pair.Value is not null)} of the "
            + $"{MandatoryHeaderCount} mandatory CloudEvents headers; missing: "
            + $"{string.Join(", ", missing)}. CloudEvents attributes are written as a set.");
    }
}
