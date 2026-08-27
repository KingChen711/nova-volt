using System.Globalization;
using Nvm.Contracts.CloudEvents;

namespace Nvm.Bus.Topology;

/// <summary>
/// The names and patterns that make up the Manufacturing Service Bus: which exchange an event goes
/// to, and how a consumer says what it wants.
/// </summary>
/// <remarks>
/// <para>
/// Topology is an architectural decision, not configuration. Once a service is publishing to
/// <c>nvm.factory-model</c> and three consumers are bound to it, the name cannot be changed without
/// coordinating every one of them — so it is decided here, once, and nobody assembles one by
/// concatenating strings at the call site.
/// </para>
/// <para>
/// One exchange per <b>bounded context</b> rather than per message type. A context is a stable unit
/// with an owner; a message type is not. Adding a fourth event to Traceability should not add a
/// fourth exchange for every consumer to discover.
/// </para>
/// </remarks>
public static class NvmTopology
{
    /// <summary>Prefix on every exchange and queue this system declares.</summary>
    public const string Prefix = "nvm";

    /// <summary>Matches exactly one segment of a routing key.</summary>
    public const string OneSegment = "*";

    /// <summary>Matches zero or more trailing segments.</summary>
    public const string AnySegments = "#";

    private const char Separator = '.';

    /// <summary>The exchange a bounded context publishes to, for example <c>nvm.factory-model</c>.</summary>
    public static string ExchangeFor(string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        return string.Concat(Prefix, Separator.ToString(), context);
    }

    /// <summary>The exchange an event type publishes to, read from its contract attributes.</summary>
    public static string ExchangeFor(EventTypeName eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return ExchangeFor(eventType.Context);
    }

    /// <summary>Everything happening at one plant: <c>nvm.NV1.#</c>.</summary>
    /// <remarks>
    /// The subscription a site-local service wants. Multiplant falls out of the routing key rather
    /// than out of a filter in every consumer — a service at Hai Phong never receives a Leipzig
    /// message in the first place, so it cannot leak one by forgetting to check.
    /// </remarks>
    public static string BindingForSite(string siteId) =>
        Join(Prefix, RequireSite(siteId), AnySegments);

    /// <summary>One context at one plant: <c>nvm.NV1.traceability.#</c>.</summary>
    public static string BindingForContext(string siteId, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        return Join(Prefix, RequireSite(siteId), context, AnySegments);
    }

    /// <summary>One event, at every plant: <c>nvm.*.factory-model.revision-activated.v1</c>.</summary>
    /// <remarks>
    /// The single-segment wildcard, not the multi-segment one. <c>nvm.#</c> would also match, and
    /// would also deliver every other event in the system to a consumer that asked for one.
    /// </remarks>
    public static string BindingForEventAtEverySite(EventTypeName eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return Join(Prefix, OneSegment, eventType.Context, eventType.Name, VersionSegment(eventType));
    }

    /// <summary>One event at one plant. The narrowest binding there is.</summary>
    public static string BindingForEvent(string siteId, EventTypeName eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return RoutingKey.Create(RequireSite(siteId), eventType).Value;
    }

    private static string VersionSegment(EventTypeName eventType) =>
        "v" + eventType.Version.ToString(CultureInfo.InvariantCulture);

    // Upper case is not cosmetic here. AMQP compares routing keys byte for byte, so a publisher on
    // nvm.NV1.* and a consumer bound to nvm.nv1.# never meet — and the broker reports nothing at all.
    // Refusing the lower-case spelling at the only place bindings are built is the moment anyone finds
    // out.
    private static string RequireSite(string siteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        return siteId.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character))
            ? siteId
            : throw new FormatException($"Site '{siteId}' must be upper-case letters and digits.");
    }

    private static string Join(params string[] segments) => string.Join(Separator, segments);
}
