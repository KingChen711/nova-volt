using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// The AMQP routing key an event is published with: <c>nvm.{site}.{context}.{event}.v{n}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Example: <c>nvm.NV1.traceability.unit-serialized.v1</c>, per docs/scope.md §7.4.
/// </para>
/// <para>
/// The shape exists so that a consumer can subscribe to exactly what it cares about — one plant, one
/// context, one version — without receiving everything and filtering in code.
/// </para>
/// <para>
/// The site keeps its upper case here, while the source URN lower-cases it. The rule behind both is
/// the same: fixed vocabulary is written in lower case, identifiers keep their canonical form. A site
/// code is an identifier, and its canonical form is upper case everywhere else in the system — in a
/// serial number, in an equipment path, in the <c>site_id</c> claim that arrives from Keycloak.
/// </para>
/// <para>
/// AMQP compares routing keys byte for byte. A publisher writing <c>nvm.NV1.…</c> and a consumer
/// binding <c>nvm.nv1.#</c> do not match, and nothing anywhere reports it: the message reaches the
/// exchange, matches no binding, and is gone. That failure is silent for as long as it takes someone
/// to ask why a report looks short. This type is therefore the only way to build the string, and it
/// rejects a lower-case site rather than quietly accepting it.
/// </para>
/// </remarks>
public sealed record RoutingKey
{
    /// <summary>Prefix shared by every routing key in the system.</summary>
    public const string Prefix = "nvm";

    private const char Separator = '.';
    private const char VersionMarker = 'v';
    private const int SegmentCount = 5;
    private const int SiteSegment = 1;
    private const int ContextSegment = 2;
    private const int NameSegment = 3;
    private const int VersionSegment = 4;

    private RoutingKey(string value, string siteId, EventTypeName eventType)
    {
        Value = value;
        SiteId = siteId;
        EventType = eventType;
    }

    /// <summary>The full routing key, exactly as it is published.</summary>
    public string Value { get; }

    /// <summary>Site code in canonical upper case, for example <c>NV1</c>.</summary>
    public string SiteId { get; }

    /// <summary>
    /// The event type this key routes. Held whole rather than as loose strings, so that a routing key
    /// and the CloudEvents <c>type</c> attribute on the same message cannot drift apart.
    /// </summary>
    public EventTypeName EventType { get; }

    /// <summary>Builds a routing key, throwing when the site is malformed.</summary>
    /// <exception cref="FormatException">The site is not upper-case alphanumeric.</exception>
    public static RoutingKey Create(string? siteId, EventTypeName eventType) =>
        TryCreate(siteId, eventType, out var key)
            ? key
            : throw new FormatException($"Not a valid routing key site: '{siteId}'. Site codes are upper case.");

    /// <summary>Builds a routing key, returning false when the site is malformed.</summary>
    public static bool TryCreate(
        [NotNullWhen(true)] string? siteId,
        EventTypeName eventType,
        [NotNullWhen(true)] out RoutingKey? key)
    {
        key = null;

        ArgumentNullException.ThrowIfNull(eventType);

        if (!Tokens.IsSiteCode(siteId))
        {
            return false;
        }

        var value = string.Join(
            Separator,
            Prefix,
            siteId,
            eventType.Context,
            eventType.Name,
            VersionMarker + eventType.Version.ToString(CultureInfo.InvariantCulture));

        key = new RoutingKey(value, siteId, eventType);
        return true;
    }

    /// <summary>Reads a routing key back from the broker, throwing when the string is malformed.</summary>
    /// <exception cref="FormatException">The string does not match the layout.</exception>
    public static RoutingKey Parse(string? value) =>
        TryParse(value, out var key)
            ? key
            : throw new FormatException("Not a valid routing key: '" + value + "'.");

    /// <summary>Reads a routing key back from the broker, returning false when the string is malformed.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out RoutingKey? key)
    {
        key = null;

        if (value is null)
        {
            return false;
        }

        var segments = value.Split(Separator);

        if (segments.Length != SegmentCount)
        {
            return false;
        }

        var versionSegment = segments[VersionSegment];

        if (versionSegment.Length < 2 || versionSegment[0] != VersionMarker)
        {
            return false;
        }

        if (!int.TryParse(versionSegment.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return false;
        }

        if (!EventTypeName.TryCreate(segments[ContextSegment], segments[NameSegment], version, out var eventType))
        {
            return false;
        }

        if (!TryCreate(segments[SiteSegment], eventType, out var candidate))
        {
            return false;
        }

        // Same reason as EventTypeName.TryParse: rebuild and compare, so a wrong prefix or a
        // lower-case site is refused instead of being repaired into something that no longer matches
        // what the publisher actually sent.
        if (!string.Equals(candidate.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>Returns the full routing key.</summary>
    public override string ToString() => Value;
}
