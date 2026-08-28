using System.Diagnostics.CodeAnalysis;
using Nvm.Kernel.Identity;

namespace Nvm.Sparkplug.Topics;

/// <summary>An MQTT topic in the Sparkplug B namespace, and the place in the plant it names.</summary>
/// <remarks>
/// <para>
/// A Sparkplug payload carries no address. Which machine sent it, and whether the message declares or
/// updates, is entirely in the topic — so this type is the join between the device world and the
/// ISA-95 tree M1 built. The mapping is fixed by docs/scope.md §7.1:
/// </para>
/// <code>
/// spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142
///         └ enterprise, site, area ┘      └line┘ └── device ──┘
///
/// NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142
///                           └ work cell, from the model ┘
/// </code>
/// <para>
/// <b>Upper case, and not by preference.</b> The same machines are also addressed by the Unified
/// Namespace tree, which is lower case: <c>novavolt/nv1/formation/f1/form-01/ch-0142</c>. Accepting
/// either spelling here would give one cycler two identities, and every count downstream would be
/// taken over half its data. <see cref="EquipmentPath"/> already refuses lower case; parsing goes
/// through it so that this type does not get a second opinion.
/// </para>
/// <para>
/// <b>The reverse direction loses the work cell.</b> A topic has four levels of plant and a path has
/// up to six, so <see cref="For"/> drops the work cell and only
/// <see cref="ResolveEquipmentPath"/> can put it back — by asking the model. That asymmetry is the
/// reason the directory exists, and the round-trip test goes through both halves rather than through
/// <see cref="For"/> alone.
/// </para>
/// </remarks>
public sealed record SparkplugTopic
{
    /// <summary>The Sparkplug B namespace, the first level of every topic.</summary>
    public const string Namespace = "spBv1.0";

    /// <summary>The prefix an edge node id carries before the line code.</summary>
    public const string EdgeNodePrefix = "EDGE-";

    private const char LevelSeparator = '/';
    private const char GroupSeparator = '-';
    private const int NodeLevelCount = 4;
    private const int DeviceLevelCount = 5;

    private SparkplugTopic(
        string value,
        SparkplugMessageType messageType,
        EquipmentPath linePath,
        string? deviceCode)
    {
        Value = value;
        MessageType = messageType;
        LinePath = linePath;
        DeviceCode = deviceCode;
    }

    /// <summary>The topic exactly as it travels on MQTT.</summary>
    public string Value { get; }

    /// <summary>What this message is for.</summary>
    public SparkplugMessageType MessageType { get; }

    /// <summary>The line the edge node speaks for, as an ISA-95 path.</summary>
    /// <remarks>
    /// Always four segments, and always what the topic <i>claims</i>. Whether the plant actually has
    /// that line is <see cref="ResolveEquipmentPath"/>'s question, not this property's.
    /// </remarks>
    public EquipmentPath LinePath { get; }

    /// <summary>The device code, or null for a node-level message.</summary>
    public string? DeviceCode { get; }

    /// <summary>The enterprise code.</summary>
    public string EnterpriseCode => LinePath.Segments[0];

    /// <summary>The plant. First class everywhere, per K3.</summary>
    public string SiteId => LinePath.Segments[1];

    /// <summary>The area code.</summary>
    public string AreaCode => LinePath.Segments[2];

    /// <summary>The line code.</summary>
    public string LineCode => LinePath.Segments[3];

    /// <summary>The Sparkplug group id, <c>{enterprise}-{site}-{area}</c>.</summary>
    public string GroupId => string.Join(GroupSeparator, EnterpriseCode, SiteId, AreaCode);

    /// <summary>The Sparkplug edge node id, <c>EDGE-{line}</c>.</summary>
    public string EdgeNodeId => EdgeNodePrefix + LineCode;

    /// <summary>Parses a topic, throwing when it is not one.</summary>
    /// <param name="value">The MQTT topic.</param>
    /// <exception cref="FormatException">The topic is not a Sparkplug B topic this system can read.</exception>
    public static SparkplugTopic Parse(string? value) =>
        TryParse(value, out var topic)
            ? topic
            : throw new FormatException($"Not a Sparkplug B topic this system can read: '{value}'.");

    /// <summary>Parses a topic, returning false when it is not one.</summary>
    /// <param name="value">The MQTT topic.</param>
    /// <param name="topic">The parsed topic.</param>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out SparkplugTopic? topic)
    {
        topic = null;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var levels = value.Split(LevelSeparator);

        if (levels.Length is not (NodeLevelCount or DeviceLevelCount))
        {
            return false;
        }

        if (!string.Equals(levels[0], Namespace, StringComparison.Ordinal))
        {
            return false;
        }

        if (!SparkplugMessageTypes.TryParse(levels[2], out var messageType))
        {
            return false;
        }

        // A DDATA with no device, or an NDATA with one, is a publisher that disagrees with the
        // specification about what it is sending. Reading it anyway means guessing which half is
        // right, on a message where the two halves say different things about the same machine.
        var deviceCode = levels.Length == DeviceLevelCount ? levels[DeviceLevelCount - 1] : null;

        if (messageType.IsDeviceLevel() != (deviceCode is not null))
        {
            return false;
        }

        // Remainder to the area, so an area code may contain a hyphen. Enterprise and site may not —
        // For() refuses to build a topic from codes that would not survive this split, which is where
        // the ambiguity is caught rather than here, where it is undetectable.
        var group = levels[1].Split(GroupSeparator, 3);

        if (group.Length != 3 || !levels[3].StartsWith(EdgeNodePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var lineCode = levels[3][EdgeNodePrefix.Length..];

        // Through EquipmentPath, which is where upper case, segment shape and depth are already
        // decided. A second set of rules here would be a second answer to the same question.
        if (!EquipmentPath.TryParse(string.Join(EquipmentPath.Separator, group[0], group[1], group[2], lineCode), out var linePath)
            || linePath.Kind != FactoryNodeKind.Line)
        {
            return false;
        }

        if (deviceCode is not null
            && !EquipmentPath.TryParse($"{linePath.Value}{EquipmentPath.Separator}{deviceCode}", out _))
        {
            return false;
        }

        topic = new SparkplugTopic(value, messageType, linePath, deviceCode);
        return true;
    }

    /// <summary>Whether a topic is the Sparkplug host state topic, <c>spBv1.0/STATE/{host}</c>.</summary>
    /// <param name="value">The MQTT topic.</param>
    /// <remarks>
    /// A different shape entirely: it names a SCADA host, not a place in the plant, so
    /// <see cref="TryParse"/> refuses it. The gateway subscribes to <c>spBv1.0/#</c> and will receive
    /// it, and it needs to tell "not addressed to us" apart from "malformed" — one is routine and the
    /// other is worth an alert.
    /// </remarks>
    public static bool IsHostState(string? value) =>
        value is not null
        && value.StartsWith($"{Namespace}{LevelSeparator}STATE{LevelSeparator}", StringComparison.Ordinal);

    /// <summary>Builds the topic a place in the plant publishes on.</summary>
    /// <param name="path">A line for a node-level message, or a work cell or equipment for a device-level one.</param>
    /// <param name="messageType">What the message is for.</param>
    /// <exception cref="ArgumentException">
    /// The path is at the wrong level for the message type, or its enterprise or site code contains a
    /// hyphen and so could not be read back out of the group id.
    /// </exception>
    /// <remarks>
    /// Needed by the simulator in C05, which publishes as the plant. It is also the half of the
    /// round-trip that can be checked without a factory model.
    /// </remarks>
    public static SparkplugTopic For(EquipmentPath path, SparkplugMessageType messageType)
    {
        ArgumentNullException.ThrowIfNull(path);

        var deviceLevel = messageType.IsDeviceLevel();

        var expected = deviceLevel
            ? path.Kind is FactoryNodeKind.WorkCell or FactoryNodeKind.Equipment
            : path.Kind is FactoryNodeKind.Line;

        if (!expected)
        {
            throw new ArgumentException(
                $"{messageType.Token()} is {(deviceLevel ? "device" : "node")}-level, so it cannot be "
                + $"published for '{path.Value}', which is a {path.Kind}.",
                nameof(path));
        }

        var enterprise = path.Segments[0];
        var site = path.Segments[1];
        var area = path.Segments[2];
        var line = path.Segments[3];

        // The group id joins three codes with the same character a code may itself contain, so the
        // split back out is only unambiguous if the first two are clean. Refused at the point of
        // building, because at the point of parsing it is invisible — the topic would simply resolve
        // to a line the plant does not have, which reads as a configuration mistake somewhere else.
        if (enterprise.Contains(GroupSeparator, StringComparison.Ordinal)
            || site.Contains(GroupSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Enterprise '{enterprise}' and site '{site}' must not contain '{GroupSeparator}': the "
                + "Sparkplug group id joins enterprise, site and area with it, and the area takes the "
                + "remainder when it is split back.",
                nameof(path));
        }

        var groupId = string.Join(GroupSeparator, enterprise, site, area);
        var value = deviceLevel
            ? string.Join(LevelSeparator, Namespace, groupId, messageType.Token(), EdgeNodePrefix + line, path.Code)
            : string.Join(LevelSeparator, Namespace, groupId, messageType.Token(), EdgeNodePrefix + line);

        var linePath = EquipmentPath.Parse(string.Join(EquipmentPath.Separator, enterprise, site, area, line));

        return new SparkplugTopic(value, messageType, linePath, deviceLevel ? path.Code : null);
    }

    /// <summary>Finds where in the plant this topic points, or null when the plant has no such place.</summary>
    /// <param name="directory">The model each plant is currently running.</param>
    /// <returns>
    /// The line for a node-level message, the work cell or equipment for a device-level one, or null.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Null rather than an exception, because this is where K3 is enforced and the answer is a routing
    /// decision, not a fault: a topic quoting a plant nobody has activated, a line that was
    /// decommissioned, or a device from a revision this plant has not been rolled out to yet all land
    /// here. Ingestion refuses the message and says which topic it refused — it does not stop.
    /// </para>
    /// <para>
    /// It is also the only way back to a six-segment path. The topic knows the device code and not the
    /// work cell holding it, so nothing but the model can say whether <c>FORM-01-CH-0142</c> hangs off
    /// the line or off a cycler.
    /// </para>
    /// </remarks>
    public EquipmentPath? ResolveEquipmentPath(IEquipmentDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (DeviceCode is null)
        {
            return directory.Contains(LinePath) ? LinePath : null;
        }

        return directory.FindDevice(LinePath, DeviceCode);
    }

    /// <summary>Returns the topic.</summary>
    public override string ToString() => Value;

    /// <summary>Compares two topics by their text.</summary>
    /// <remarks>
    /// The record's generated equality would compare <see cref="LinePath"/> by reference and call two
    /// identical topics different. Ordinal, for the same reason the paths are: these are machine
    /// addresses, and a culture-aware comparison can decide two different machines match.
    /// </remarks>
    public bool Equals(SparkplugTopic? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}
