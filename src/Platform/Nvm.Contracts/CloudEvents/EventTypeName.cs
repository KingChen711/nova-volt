using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// The CloudEvents <c>type</c> attribute: <c>com.novavolt.{context}.{event}.v{n}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Example: <c>com.novavolt.traceability.unit-serialized.v1</c>, per docs/scope.md §7.4.
/// </para>
/// <para>
/// Reverse-DNS prefix, then the bounded context that owns the event, then the event in kebab-case,
/// then the schema version. The version is part of the name rather than a separate field because a
/// consumer decides whether it can read a message by looking at one string — and a message that
/// cannot be deserialized has nothing but its headers left to look at.
/// </para>
/// <para>
/// Parsing matters as much as formatting. When a message lands in the <c>_error</c> queue, the
/// payload is by definition something the code could not understand; reading <c>type</c> back out of
/// the header is how an operator finds out what it was supposed to be.
/// </para>
/// </remarks>
public sealed record EventTypeName
{
    /// <summary>Reverse-DNS prefix shared by every event type in the system.</summary>
    public const string Prefix = "com.novavolt";

    private const char Separator = '.';
    private const char VersionMarker = 'v';
    private const int SegmentCount = 5;
    private const int ContextSegment = 2;
    private const int NameSegment = 3;
    private const int VersionSegment = 4;

    private EventTypeName(string value, string context, string name, int version)
    {
        Value = value;
        Context = context;
        Name = name;
        Version = version;
    }

    /// <summary>The full type string, exactly as it travels on the wire.</summary>
    public string Value { get; }

    /// <summary>Bounded context that owns the event, for example <c>traceability</c>.</summary>
    public string Context { get; }

    /// <summary>Event name in kebab-case, for example <c>unit-serialized</c>.</summary>
    public string Name { get; }

    /// <summary>Schema version, starting at 1. Matches the type's <c>EventVersionAttribute</c>.</summary>
    public int Version { get; }

    /// <summary>Builds a type name from its parts, throwing when any part is malformed.</summary>
    /// <exception cref="FormatException">A segment is not lower-case kebab-case, or the version is below 1.</exception>
    public static EventTypeName Create(string? context, string? name, int version) =>
        TryCreate(context, name, version, out var type)
            ? type
            : throw new FormatException(
                $"Not a valid event type: context '{context}', name '{name}', version {version.ToString(CultureInfo.InvariantCulture)}.");

    /// <summary>Builds a type name from its parts, returning false when any part is malformed.</summary>
    public static bool TryCreate(
        [NotNullWhen(true)] string? context,
        [NotNullWhen(true)] string? name,
        int version,
        [NotNullWhen(true)] out EventTypeName? type)
    {
        type = null;

        if (!Tokens.IsFixedVocabulary(context) || !Tokens.IsFixedVocabulary(name) || version < 1)
        {
            return false;
        }

        var value = string.Concat(
            Prefix,
            Separator,
            context,
            Separator,
            name,
            Separator,
            VersionMarker,
            version.ToString(CultureInfo.InvariantCulture));

        type = new EventTypeName(value, context, name, version);
        return true;
    }

    /// <summary>Reads a type name back from the wire, throwing when the string is malformed.</summary>
    /// <exception cref="FormatException">The string does not match the layout.</exception>
    public static EventTypeName Parse(string? value) =>
        TryParse(value, out var type)
            ? type
            : throw new FormatException("Not a valid event type name: '" + value + "'.");

    /// <summary>Reads a type name back from the wire, returning false when the string is malformed.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out EventTypeName? type)
    {
        type = null;

        if (value is null)
        {
            return false;
        }

        var segments = value.Split(Separator);

        if (segments.Length != SegmentCount)
        {
            return false;
        }

        if (!TryReadVersion(segments[VersionSegment], out var version))
        {
            return false;
        }

        if (!TryCreate(segments[ContextSegment], segments[NameSegment], version, out var candidate))
        {
            return false;
        }

        // Rebuilding and comparing is what makes this a parser rather than a lenient reader. Anything
        // that would round-trip to a different string — "v01", a wrong prefix, a stray plus sign the
        // number parser tolerates — is rejected here instead of being silently normalised into the
        // store. One event type must have exactly one spelling, forever.
        if (!string.Equals(candidate.Value, value, StringComparison.Ordinal))
        {
            return false;
        }

        type = candidate;
        return true;
    }

    /// <summary>Returns the full type string.</summary>
    public override string ToString() => Value;

    private static bool TryReadVersion(string segment, out int version)
    {
        version = 0;

        if (segment.Length < 2 || segment[0] != VersionMarker)
        {
            return false;
        }

        return int.TryParse(
            segment.AsSpan(1),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out version);
    }
}
