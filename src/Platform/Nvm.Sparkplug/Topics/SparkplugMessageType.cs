namespace Nvm.Sparkplug.Topics;

/// <summary>What a Sparkplug message is for, taken from the topic.</summary>
/// <remarks>
/// The payload does not say. Everything the decoder needs in order to know whether it is looking at a
/// declaration or an update comes from here, which is why <c>SparkplugPayload</c> has two entry points
/// and no guessing.
/// </remarks>
public enum SparkplugMessageType
{
    /// <summary><c>NBIRTH</c> — an edge node came online and is declaring itself.</summary>
    NodeBirth,

    /// <summary><c>NDEATH</c> — the broker published the node's last will; it is gone.</summary>
    NodeDeath,

    /// <summary><c>NDATA</c> — values that changed on the node itself.</summary>
    NodeData,

    /// <summary><c>NCMD</c> — a command to the node. Rebirth requests travel this way.</summary>
    NodeCommand,

    /// <summary><c>DBIRTH</c> — a device under the node is declaring its metrics.</summary>
    DeviceBirth,

    /// <summary><c>DDEATH</c> — a device under the node stopped reporting.</summary>
    DeviceDeath,

    /// <summary><c>DDATA</c> — values that changed on a device.</summary>
    DeviceData,

    /// <summary><c>DCMD</c> — a command to a device.</summary>
    DeviceCommand,
}

/// <summary>Converts between the enum and the token that appears in a topic.</summary>
/// <remarks>
/// Written out rather than reached through <c>Enum.Parse</c>. The wire tokens are upper case and
/// abbreviated (<c>NBIRTH</c>, not <c>NodeBirth</c>), and a case-insensitive parse would quietly
/// accept <c>nbirth</c> — a topic from the lower-case Unified Namespace tree, which addresses the same
/// machines by a different convention (docs/scope.md §7.1). Accepting both spellings is how one
/// machine ends up with two identities.
/// </remarks>
public static class SparkplugMessageTypes
{
    /// <summary>The token this message type appears as in a topic.</summary>
    /// <param name="messageType">The message type.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a message type.</exception>
    public static string Token(this SparkplugMessageType messageType) =>
        messageType switch
        {
            SparkplugMessageType.NodeBirth => "NBIRTH",
            SparkplugMessageType.NodeDeath => "NDEATH",
            SparkplugMessageType.NodeData => "NDATA",
            SparkplugMessageType.NodeCommand => "NCMD",
            SparkplugMessageType.DeviceBirth => "DBIRTH",
            SparkplugMessageType.DeviceDeath => "DDEATH",
            SparkplugMessageType.DeviceData => "DDATA",
            SparkplugMessageType.DeviceCommand => "DCMD",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType)),
        };

    /// <summary>Whether this message type addresses a device rather than the edge node itself.</summary>
    /// <param name="messageType">The message type.</param>
    /// <remarks>
    /// Decides how many levels the topic has, so it is also what catches a <c>DDATA</c> published
    /// without a device or an <c>NDATA</c> published with one.
    /// </remarks>
    public static bool IsDeviceLevel(this SparkplugMessageType messageType) =>
        messageType
            is SparkplugMessageType.DeviceBirth
            or SparkplugMessageType.DeviceDeath
            or SparkplugMessageType.DeviceData
            or SparkplugMessageType.DeviceCommand;

    /// <summary>Reads a topic token, case-sensitively.</summary>
    /// <param name="token">The token from the topic.</param>
    /// <param name="messageType">The message type it names.</param>
    /// <returns><see langword="false"/> when the token is not one Sparkplug defines.</returns>
    public static bool TryParse(string? token, out SparkplugMessageType messageType)
    {
        var parsed = token switch
        {
            "NBIRTH" => SparkplugMessageType.NodeBirth,
            "NDEATH" => SparkplugMessageType.NodeDeath,
            "NDATA" => SparkplugMessageType.NodeData,
            "NCMD" => SparkplugMessageType.NodeCommand,
            "DBIRTH" => SparkplugMessageType.DeviceBirth,
            "DDEATH" => SparkplugMessageType.DeviceDeath,
            "DDATA" => SparkplugMessageType.DeviceData,
            "DCMD" => SparkplugMessageType.DeviceCommand,
            _ => (SparkplugMessageType?)null,
        };

        messageType = parsed ?? default;

        return parsed is not null;
    }
}
