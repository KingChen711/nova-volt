using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.EdgeGateway.Sessions;

/// <summary>Which edge node a message came from, independent of the device under it.</summary>
/// <param name="LinePath">The line the node speaks for, which is the identity the plant uses.</param>
/// <remarks>
/// <para>
/// <c>seq</c>, <c>bdSeq</c> and liveness are all properties of the <b>node</b>, never of a device.
/// One counter per device would make a gap in it meaningless, and a death would only ever be able to
/// kill the box that noticed it.
/// </para>
/// <para>
/// The line path rather than the group and node strings, even though the wire carries those: it is
/// the only form that can be handed back to <see cref="SparkplugTopic.For"/> to address the node,
/// and keeping both would let the two drift apart.
/// </para>
/// </remarks>
public readonly record struct NodeAddress(EquipmentPath LinePath)
{
    /// <summary>The Sparkplug group, which encodes enterprise, site and area.</summary>
    public string GroupId => SparkplugTopic.For(LinePath, SparkplugMessageType.NodeCommand).GroupId;

    /// <summary>The edge node id as it appears in a topic.</summary>
    public string EdgeNodeId => SparkplugTopic.EdgeNodePrefix + LinePath.Code;

    /// <summary>The plant this node belongs to (K3).</summary>
    public string SiteId => LinePath.Segments[1];

    /// <summary>Reads the node a topic addresses, device level or not.</summary>
    /// <param name="topic">Any Sparkplug topic.</param>
    public static NodeAddress From(SparkplugTopic topic)
    {
        ArgumentNullException.ThrowIfNull(topic);

        return new NodeAddress(topic.LinePath);
    }

    /// <summary>The topic a command to this node is published on.</summary>
    public SparkplugTopic NodeCommandTopic() =>
        SparkplugTopic.For(LinePath, SparkplugMessageType.NodeCommand);

    /// <inheritdoc />
    public override string ToString() => LinePath.Value;
}

/// <summary>How much a reported value can be trusted right now.</summary>
/// <remarks>
/// Three states, because a control room reading a formation channel has three questions and only one
/// of them is about the cell. <c>0</c> reads as "the voltage is zero" and starts an alarm on healthy
/// hardware; <c>null</c> reads as "nothing has been measured yet" and is ignored. Only
/// <see cref="Stale"/> says what is actually true after an <c>NDEATH</c>: the last number was X at
/// time T, and nothing since then can be believed. That sends someone to check the network instead
/// of the cell.
/// </remarks>
public enum NodeLiveness
{
    /// <summary>No birth has been seen for this node in this gateway's lifetime.</summary>
    Unknown,

    /// <summary>The node published a birth and has not died since.</summary>
    Online,

    /// <summary>The node's last will fired. Its history stands; its present does not.</summary>
    Stale,
}

/// <summary>The last thing a metric said, and whether that is still current.</summary>
/// <param name="MetricName">The metric, by the name its birth declared.</param>
/// <param name="LastValue">The most recent value seen.</param>
/// <param name="LastDeviceTimestamp">When the device says it took that value.</param>
/// <param name="Liveness">Whether the node behind it is still alive.</param>
public sealed record NodeMetricState(
    string MetricName,
    MetricValue LastValue,
    DateTimeOffset LastDeviceTimestamp,
    NodeLiveness Liveness);

/// <summary>What the gateway currently believes about one edge node.</summary>
/// <param name="Address">The node.</param>
/// <param name="Liveness">Alive, dead, or never seen.</param>
/// <param name="BirthDeathSequence">The session number of the birth in force, when it carried one.</param>
/// <param name="LastSequence">The last <c>seq</c> accepted from this node.</param>
/// <param name="StaleSince">When the death was processed, or null while the node is alive.</param>
/// <param name="SequenceGaps">How many times a message arrived out of order for this node.</param>
/// <param name="Metrics">Every metric of every device under the node, newest value first seen last.</param>
public sealed record NodeSessionSnapshot(
    NodeAddress Address,
    NodeLiveness Liveness,
    ulong? BirthDeathSequence,
    ulong? LastSequence,
    DateTimeOffset? StaleSince,
    long SequenceGaps,
    ImmutableArray<NodeMetricState> Metrics);
