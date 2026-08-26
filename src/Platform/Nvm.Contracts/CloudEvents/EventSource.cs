using System.Diagnostics.CodeAnalysis;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// The CloudEvents <c>source</c> attribute: <c>urn:novavolt:{site}:{application}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Example: <c>urn:novavolt:nv1:app-execution</c>, per docs/scope.md §7.4.
/// </para>
/// <para>
/// Source answers "who says so". Together with the event id it is what CloudEvents defines
/// uniqueness against, and when two services disagree about the same unit it is the first thing
/// anyone looks at.
/// </para>
/// <para>
/// The site appears here in lower case, and in the routing key in upper case. That is not an
/// oversight — see <see cref="RoutingKey"/>. Inside this type the site is always held in its
/// canonical upper-case form, so that <see cref="SiteId"/> can be compared against every other
/// SiteId in the codebase without anyone remembering which string came from a URN.
/// </para>
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(EventSourceJsonConverter))]
public sealed record EventSource
{
    /// <summary>URN prefix shared by every source in the system.</summary>
    public const string Prefix = "urn:novavolt";

    private const char Separator = ':';
    private const int SegmentCount = 4;
    private const int SiteSegment = 2;
    private const int ApplicationSegment = 3;

    private EventSource(string value, string siteId, string application)
    {
        Value = value;
        SiteId = siteId;
        Application = application;
    }

    /// <summary>The full URN, exactly as it travels on the wire, with the site in lower case.</summary>
    public string Value { get; }

    /// <summary>Site code in its canonical upper-case form, for example <c>NV1</c>.</summary>
    public string SiteId { get; }

    /// <summary>Deployable that published the event, for example <c>app-execution</c>.</summary>
    public string Application { get; }

    /// <summary>Builds a source URN, throwing when a part is malformed.</summary>
    /// <exception cref="FormatException">The site is not upper-case alphanumeric, or the application is not kebab-case.</exception>
    public static EventSource Create(string? siteId, string? application) =>
        TryCreate(siteId, application, out var source)
            ? source
            : throw new FormatException($"Not a valid event source: site '{siteId}', application '{application}'.");

    /// <summary>Builds a source URN, returning false when a part is malformed.</summary>
    public static bool TryCreate(
        [NotNullWhen(true)] string? siteId,
        [NotNullWhen(true)] string? application,
        [NotNullWhen(true)] out EventSource? source)
    {
        source = null;

        if (!Tokens.IsSiteCode(siteId) || !Tokens.IsFixedVocabulary(application))
        {
            return false;
        }

        // ToLowerInvariant, never ToLower: with InvariantGlobalization off (ADR-020) the ambient
        // culture is real, and on a Turkish machine ToLower turns 'I' into 'ı'. A source URN that
        // depends on where the process runs is not an identifier.
        var value = string.Concat(Prefix, Separator, siteId.ToLowerInvariant(), Separator, application);

        source = new EventSource(value, siteId, application);
        return true;
    }

    /// <summary>Reads a source URN back from the wire, throwing when the string is malformed.</summary>
    /// <exception cref="FormatException">The string does not match the layout.</exception>
    public static EventSource Parse(string? value) =>
        TryParse(value, out var source)
            ? source
            : throw new FormatException("Not a valid event source: '" + value + "'.");

    /// <summary>Reads a source URN back from the wire, returning false when the string is malformed.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out EventSource? source)
    {
        source = null;

        if (value is null)
        {
            return false;
        }

        var segments = value.Split(Separator);

        if (segments.Length != SegmentCount)
        {
            return false;
        }

        // The site travels lower-cased, so it is lifted back to canonical form before validation.
        // Upper-casing here is safe because a site code is alphanumeric by definition; anything that
        // was not already lower case will fail the round-trip check below.
        if (!TryCreate(segments[SiteSegment].ToUpperInvariant(), segments[ApplicationSegment], out var candidate))
        {
            return false;
        }

        if (!string.Equals(candidate.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        source = candidate;
        return true;
    }

    /// <summary>Returns the full URN.</summary>
    public override string ToString() => Value;
}
