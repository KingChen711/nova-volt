using System.Collections.Immutable;
using System.Threading.Channels;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.EdgeGateway.Sessions;

/// <summary>Knows which nodes are alive, which aliases are readable, and when data was missed.</summary>
/// <remarks>
/// <para>
/// Three jobs that look separate and are not. All three hang off one fact: an alias table, a
/// sequence counter and a liveness flag belong to <b>one session of one edge node</b>, and they all
/// become worthless at the same instant — when that session ends. Splitting them across three owners
/// is how a system ends up resolving this session's aliases against the previous session's table.
/// </para>
/// <para>
/// State is kept in memory on purpose. It describes what is true <i>now</i> on the plant floor, and
/// a gateway that restarts genuinely does not know: the correct answer after a restart is
/// <see cref="NodeLiveness.Unknown"/> until a birth arrives, not a stale row read back from disk.
/// Historical readings are a different question and live in the database, untouched by any of this
/// (K4).
/// </para>
/// </remarks>
public sealed class NodeSessionTracker
{
    private readonly Dictionary<NodeAddress, NodeState> _nodes = [];
    private readonly Lock _gate = new();
    private readonly Channel<NodeAddress> _rebirthRequests;
    private readonly GatewayCounters _counters;
    private readonly TimeProvider _clock;

    /// <summary>Creates the tracker with a bounded queue of pending rebirth requests.</summary>
    /// <param name="counters">Process counters for rebirths and ignored late deaths.</param>
    /// <param name="clock">Clock stamping the moment a node went stale (K1).</param>
    public NodeSessionTracker(GatewayCounters counters, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(clock);

        _counters = counters;
        _clock = clock;

        // Dropping the oldest is right for this queue and only this queue: a rebirth request is a
        // statement about now, and a stale one asks a node to re-declare something it has already
        // re-declared. Nothing durable is lost — unlike the store-and-forward buffer, where dropping
        // is exactly what ADR-028 forbids.
        _rebirthRequests = Channel.CreateBounded<NodeAddress>(
            new BoundedChannelOptions(capacity: 256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
    }

    /// <summary>Rebirth requests waiting to be published as <c>NCMD</c>.</summary>
    public ChannelReader<NodeAddress> RebirthRequests => _rebirthRequests.Reader;

    /// <summary>How many nodes are currently believed dead.</summary>
    public int StaleNodeCount
    {
        get
        {
            lock (_gate)
            {
                return _nodes.Values.Count(node => node.Liveness == NodeLiveness.Stale);
            }
        }
    }

    /// <summary>The alias table in force for the publisher of this topic.</summary>
    /// <param name="topic">The topic of the data message about to be decoded.</param>
    /// <returns>The table its birth installed, or <see cref="MetricAliasTable.Empty"/>.</returns>
    public MetricAliasTable AliasesFor(SparkplugTopic topic)
    {
        ArgumentNullException.ThrowIfNull(topic);

        lock (_gate)
        {
            return _nodes.TryGetValue(NodeAddress.From(topic), out var node)
                ? node.AliasesFor(topic.DeviceCode)
                : MetricAliasTable.Empty;
        }
    }

    /// <summary>Applies a birth: opens or replaces the session and installs its aliases.</summary>
    /// <param name="topic">The <c>NBIRTH</c> or <c>DBIRTH</c> topic.</param>
    /// <param name="birth">The decoded birth.</param>
    public void ObserveBirth(SparkplugTopic topic, SparkplugBirth birth)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(birth);

        var address = NodeAddress.From(topic);

        lock (_gate)
        {
            if (!_nodes.TryGetValue(address, out var node))
            {
                node = new NodeState();
                _nodes[address] = node;
            }

            if (topic.MessageType == SparkplugMessageType.NodeBirth)
            {
                // A node birth ends the previous session outright. Keeping the old device tables
                // "just in case" is precisely the bug alias numbering makes possible: the new session
                // is free to give 1 to a different metric, and every unmatched device would file its
                // readings under the previous meaning without one error being raised.
                node.OpenSession(birth.BirthDeathSequence);
            }

            node.InstallAliases(topic.DeviceCode, birth.Aliases);
            node.RecordReadings(topic.DeviceCode, birth.Readings);
            ApplySequence(address, node, birth.Sequence);
        }
    }

    /// <summary>Applies a data message: refreshes values and checks the sequence.</summary>
    /// <param name="topic">The <c>NDATA</c> or <c>DDATA</c> topic.</param>
    /// <param name="readings">The decoded readings.</param>
    /// <param name="sequence">The payload <c>seq</c>, when it carried one.</param>
    public void ObserveData(SparkplugTopic topic, ImmutableArray<DeviceReading> readings, ulong? sequence)
    {
        ArgumentNullException.ThrowIfNull(topic);

        var address = NodeAddress.From(topic);

        lock (_gate)
        {
            if (!_nodes.TryGetValue(address, out var node))
            {
                node = new NodeState();
                _nodes[address] = node;
            }

            node.RecordReadings(topic.DeviceCode, readings);
            ApplySequence(address, node, sequence);
        }
    }

    /// <summary>Applies a death: marks every metric of the node stale, and deletes nothing.</summary>
    /// <param name="topic">The <c>NDEATH</c> topic.</param>
    /// <param name="death">The decoded last will.</param>
    /// <returns><see langword="true"/> when this death ended the session currently in force.</returns>
    /// <remarks>
    /// The <c>bdSeq</c> check is the whole reason a death carries one. A broker that held a will
    /// while the node reconnected will deliver the death of session 6 after the birth of session 7,
    /// and a gateway that did not compare would mark a node dead that is, at that moment, publishing.
    /// </remarks>
    public bool ObserveDeath(SparkplugTopic topic, SparkplugDeath death)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(death);

        var address = NodeAddress.From(topic);

        lock (_gate)
        {
            if (!_nodes.TryGetValue(address, out var node))
            {
                // A death for a node this gateway never saw born. Recorded rather than dropped: the
                // node is genuinely not reporting, and "unknown" would claim we have no opinion.
                node = new NodeState();
                _nodes[address] = node;
            }

            if (node.BirthDeathSequence is { } current
                && death.BirthDeathSequence is { } dying
                && current != dying)
            {
                _counters.CountLateDeathIgnored();
                return false;
            }

            node.MarkStale(_clock.GetUtcNow());
            return true;
        }
    }

    /// <summary>Asks a node to declare itself again, at most once per detected gap.</summary>
    /// <param name="topic">Any topic of the node to ask.</param>
    /// <remarks>
    /// Also the right answer to an alias the table cannot resolve: both mean the same thing — this
    /// gateway's picture of the session is behind the node's — and both are only repairable by the
    /// node saying everything again.
    /// </remarks>
    public void RequestRebirth(SparkplugTopic topic)
    {
        ArgumentNullException.ThrowIfNull(topic);

        RequestRebirth(NodeAddress.From(topic));
    }

    /// <summary>What the gateway believes about one node right now.</summary>
    /// <param name="address">The node to describe.</param>
    public NodeSessionSnapshot Snapshot(NodeAddress address)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(address, out var node)
                ? node.ToSnapshot(address)
                : new NodeSessionSnapshot(
                    address,
                    NodeLiveness.Unknown,
                    BirthDeathSequence: null,
                    LastSequence: null,
                    StaleSince: null,
                    SequenceGaps: 0,
                    Metrics: []);
        }
    }

    /// <summary>Every node this gateway has an opinion about.</summary>
    public ImmutableArray<NodeSessionSnapshot> Snapshots()
    {
        lock (_gate)
        {
            return [.. _nodes.Select(entry => entry.Value.ToSnapshot(entry.Key))];
        }
    }

    private void RequestRebirth(NodeAddress address)
    {
        _counters.CountRebirthRequested();
        _rebirthRequests.Writer.TryWrite(address);
    }

    private void ApplySequence(NodeAddress address, NodeState node, ulong? sequence)
    {
        if (sequence is not { } observed || !node.IsSequenceGap(observed))
        {
            node.AcceptSequence(sequence);
            return;
        }

        node.AcceptSequence(sequence);
        node.CountGap();
        RequestRebirth(address);
    }

    private sealed class NodeState
    {
        // seq is one byte on the wire and wraps at 256, so "went backwards" is never a valid reading
        // of a smaller number — only "did the count advance by exactly one" is.
        private const ulong SequenceWrap = 256;

        private readonly Dictionary<string, Dictionary<string, NodeMetricState>> _metrics =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, MetricAliasTable> _aliases = new(StringComparer.Ordinal);

        internal NodeLiveness Liveness { get; private set; } = NodeLiveness.Unknown;

        internal ulong? BirthDeathSequence { get; private set; }

        internal ulong? LastSequence { get; private set; }

        internal DateTimeOffset? StaleSince { get; private set; }

        internal long SequenceGaps { get; private set; }

        internal void OpenSession(ulong? birthDeathSequence)
        {
            _aliases.Clear();
            _metrics.Clear();
            BirthDeathSequence = birthDeathSequence;
            Liveness = NodeLiveness.Online;
            StaleSince = null;
            LastSequence = null;
        }

        internal void InstallAliases(string? deviceCode, MetricAliasTable aliases) =>
            _aliases[DeviceKey(deviceCode)] = aliases;

        internal MetricAliasTable AliasesFor(string? deviceCode) =>
            _aliases.GetValueOrDefault(DeviceKey(deviceCode), MetricAliasTable.Empty);

        internal void RecordReadings(string? deviceCode, ImmutableArray<DeviceReading> readings)
        {
            if (readings.IsDefaultOrEmpty)
            {
                return;
            }

            // Data arriving means the node is talking, whatever we believed a moment ago. A gateway
            // that stayed on Stale while readings flowed would keep a control room chasing a network
            // fault that has already fixed itself.
            if (Liveness != NodeLiveness.Online)
            {
                Liveness = NodeLiveness.Online;
                StaleSince = null;
            }

            var device = _metrics.TryGetValue(DeviceKey(deviceCode), out var existing)
                ? existing
                : _metrics[DeviceKey(deviceCode)] = new Dictionary<string, NodeMetricState>(StringComparer.Ordinal);

            foreach (var reading in readings)
            {
                // Protocol metrics describe the session, and the session already has a home on this
                // object. Leaving them in the metric picture would put "bdSeq is STALE" in front of
                // an operator, which is true and useless.
                if (SparkplugPayload.IsProtocolMetric(reading.MetricName))
                {
                    continue;
                }

                device[reading.MetricName] = new NodeMetricState(
                    reading.MetricName,
                    reading.Value,
                    reading.DeviceTimestamp,
                    NodeLiveness.Online);
            }
        }

        internal void MarkStale(DateTimeOffset at)
        {
            Liveness = NodeLiveness.Stale;
            StaleSince = at;

            // The last value and its timestamp survive, and that is the whole point. "3,82 V at
            // 09:14, and not trustworthy since" is a different statement from "no data" and from
            // "zero volts", and only the first one sends the right person to the right place.
            foreach (var device in _metrics.Values)
            {
                foreach (var metricName in device.Keys.ToArray())
                {
                    device[metricName] = device[metricName] with { Liveness = NodeLiveness.Stale };
                }
            }
        }

        internal bool IsSequenceGap(ulong observed) =>
            LastSequence is { } last && observed != (last + 1) % SequenceWrap;

        internal void AcceptSequence(ulong? sequence)
        {
            if (sequence is { } observed)
            {
                LastSequence = observed;
            }
        }

        internal void CountGap() => SequenceGaps++;

        internal NodeSessionSnapshot ToSnapshot(NodeAddress address) =>
            new(
                address,
                Liveness,
                BirthDeathSequence,
                LastSequence,
                StaleSince,
                SequenceGaps,
                [.. _metrics.Values.SelectMany(device => device.Values)]);

        private static string DeviceKey(string? deviceCode) => deviceCode ?? string.Empty;
    }
}
