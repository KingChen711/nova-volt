using System.Diagnostics.CodeAnalysis;

namespace Nvm.Kernel.Identity;

/// <summary>
/// A position in the ISA-95 hierarchy, written as
/// <c>NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142</c>.
/// </summary>
/// <remarks>
/// <para>
/// The single most reused string in the system (docs/scope.md §2.1): it is the MQTT topic, the metric
/// label, the authorization key, the XPath constraint in Mendix, and the <c>equipmentId</c> field of
/// an event. Decided once, used everywhere — which also means a mistake here is a mistake in six
/// places.
/// </para>
/// <para>
/// A path may stop at any level. An area has a perfectly good path, and a supervisor's dashboard asks
/// questions at that level while a recall asks them at equipment level. Both use this type, and the
/// level is read off the number of segments rather than stored separately.
/// </para>
/// <para>
/// Comparison is case-sensitive and lower case is rejected rather than normalised. The codes are
/// stencilled on machines and printed on routing sheets in upper case; a lower-case reading means
/// something upstream is misconfigured. Quietly upper-casing it would give one machine two nodes in
/// the tree and split its history down the middle.
/// </para>
/// <para>
/// The name is the plant's, not an invention: <c>equipment_path</c> is what the term is on the shop
/// floor, even for a path that stops at an area.
/// </para>
/// </remarks>
public sealed record EquipmentPath
{
    /// <summary>The character between segments.</summary>
    public const char Separator = '/';

    private const int MinSegments = (int)FactoryNodeKind.Enterprise;
    private const int MaxSegments = (int)FactoryNodeKind.Equipment;
    private const int SiteSegmentIndex = (int)FactoryNodeKind.Site - 1;

    private readonly string[] _segments;

    private EquipmentPath(string value, string[] segments)
    {
        Value = value;
        _segments = segments;
    }

    /// <summary>The full path, exactly as it is written everywhere else.</summary>
    public string Value { get; }

    /// <summary>Which level of the hierarchy this path names, taken from its depth.</summary>
    public FactoryNodeKind Kind => (FactoryNodeKind)_segments.Length;

    /// <summary>The segments, outermost first.</summary>
    public IReadOnlyList<string> Segments => _segments;

    /// <summary>The last segment: the code of the thing this path names.</summary>
    public string Code => _segments[^1];

    /// <summary>The enterprise code, always the first segment.</summary>
    public string EnterpriseCode => _segments[0];

    /// <summary>
    /// The plant this path belongs to, or null for an enterprise-level path.
    /// </summary>
    /// <remarks>
    /// Null is possible for exactly one level, and callers have to deal with it rather than assume it
    /// away (AGENTS.md K3). An enterprise spans plants, so asking which plant it is in has no answer —
    /// unlike every level below, where the answer is mandatory.
    /// </remarks>
    public string? SiteId => _segments.Length > SiteSegmentIndex ? _segments[SiteSegmentIndex] : null;

    /// <summary>The path one level up, or null when this is already the enterprise.</summary>
    public EquipmentPath? Parent =>
        _segments.Length == MinSegments
            ? null
            : new EquipmentPath(
                string.Join(Separator, _segments[..^1]),
                _segments[..^1]);

    /// <summary>Parses a path, throwing when it is malformed.</summary>
    /// <exception cref="FormatException">The string does not describe a position in the hierarchy.</exception>
    public static EquipmentPath Parse(string? value) =>
        TryParse(value, out var path)
            ? path
            : throw new FormatException("Not a valid equipment path: '" + value + "'.");

    /// <summary>Parses a path, returning false when it is malformed.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out EquipmentPath? path)
    {
        path = null;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var segments = value.Split(Separator);

        // Below one segment there is nothing to name; above six there is no level for it to be. The
        // hierarchy is fixed at six by docs/scope.md §2.1 — sub-components of a machine are modelled
        // as attributes of the equipment, not as a seventh level.
        if (segments.Length is < MinSegments or > MaxSegments)
        {
            return false;
        }

        if (Array.Exists(segments, segment => !IsValidSegment(segment)))
        {
            return false;
        }

        path = new EquipmentPath(value, segments);
        return true;
    }

    /// <summary>Builds the path of a child one level down.</summary>
    /// <param name="childCode">The child's code, for example <c>FORM-01</c>.</param>
    /// <exception cref="FormatException">The code is malformed.</exception>
    /// <exception cref="InvalidOperationException">This path is already at the deepest level.</exception>
    public EquipmentPath Append(string childCode)
    {
        if (!IsValidSegment(childCode))
        {
            throw new FormatException("Not a valid path segment: '" + childCode + "'.");
        }

        if (_segments.Length == MaxSegments)
        {
            throw new InvalidOperationException(
                $"'{Value}' is already {FactoryNodeKind.Equipment}, the deepest level of the hierarchy.");
        }

        return new EquipmentPath(
            string.Concat(Value, Separator.ToString(), childCode),
            [.. _segments, childCode]);
    }

    /// <summary>Returns the full path.</summary>
    public override string ToString() => Value;

    /// <summary>Compares two paths by their text.</summary>
    /// <remarks>
    /// The record's generated equality would compare the segment array by reference and report two
    /// identical paths as different. Ordinal comparison is also the right one: these are machine
    /// codes, not words, and a culture-aware comparison could decide that two different machines have
    /// the same name.
    /// </remarks>
    public bool Equals(EquipmentPath? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    private static bool IsValidSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return false;
        }

        // Opens with an upper-case letter and closes with a letter or digit. Rejects an empty segment
        // from a doubled separator, a leading or trailing hyphen, and any lower case at all.
        if (!char.IsAsciiLetterUpper(segment[0]) || segment[^1] == '-')
        {
            return false;
        }

        for (var index = 1; index < segment.Length; index++)
        {
            var character = segment[index];

            if (char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character))
            {
                continue;
            }

            // A single hyphen inside a code is normal — FORM-01-CH-0142. A doubled one is a second
            // spelling of the same machine waiting to happen.
            if (character == '-' && segment[index - 1] != '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
