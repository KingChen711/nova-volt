using System.Collections.Immutable;
using System.Threading.Channels;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.EdgeGateway.Sessions;

/// <summary>Biết node nào đang sống, alias nào đọc được, và khi nào dữ liệu bị bỏ lỡ.</summary>
/// <remarks>
/// <para>
/// Ba việc trông có vẻ tách biệt nhưng không phải vậy. Cả ba đều dựa trên một sự thật duy nhất:
/// một bảng alias, một sequence counter và một cờ liveness thuộc về <b>một session của một edge
/// node</b>, và cả ba đều trở nên vô giá trị tại cùng một thời điểm — khi session đó kết thúc.
/// Tách chúng ra cho ba chủ sở hữu khác nhau là cách một hệ thống cuối cùng lại resolve alias của
/// session hiện tại dựa vào bảng của session trước đó.
/// </para>
/// <para>
/// State được cố tình giữ trong bộ nhớ. Nó mô tả điều gì đúng <i>ngay bây giờ</i> trên sàn nhà
/// máy, và một gateway khi restart thực sự không biết điều đó: câu trả lời đúng sau một lần
/// restart là <see cref="NodeLiveness.Unknown"/> cho tới khi một birth tới, không phải một dòng
/// stale đọc lại từ đĩa. Các reading lịch sử là một câu chuyện khác và sống trong database, không
/// bị đụng chạm bởi bất cứ điều gì ở đây (K4).
/// </para>
/// </remarks>
public sealed class NodeSessionTracker
{
    private readonly Dictionary<NodeAddress, NodeState> _nodes = [];
    private readonly Lock _gate = new();
    private readonly Channel<NodeAddress> _rebirthRequests;
    private readonly GatewayCounters _counters;
    private readonly TimeProvider _clock;

    /// <summary>Tạo tracker với một hàng đợi có giới hạn chứa các rebirth request đang chờ.</summary>
    /// <param name="counters">Process counter cho rebirth và các late death bị bỏ qua.</param>
    /// <param name="clock">Đồng hồ đóng dấu thời điểm một node trở nên stale (K1).</param>
    public NodeSessionTracker(GatewayCounters counters, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(clock);

        _counters = counters;
        _clock = clock;

        // Drop cái cũ nhất là đúng cho hàng đợi này và chỉ hàng đợi này: một rebirth request là một
        // phát biểu về hiện tại, và một request đã cũ chỉ yêu cầu một node khai báo lại thứ nó đã
        // khai báo lại rồi. Không có gì durable bị mất — khác với buffer store-and-forward, nơi
        // việc drop chính là điều ADR-028 cấm.
        _rebirthRequests = Channel.CreateBounded<NodeAddress>(
            new BoundedChannelOptions(capacity: 256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
    }

    /// <summary>Các rebirth request đang chờ được publish dưới dạng <c>NCMD</c>.</summary>
    public ChannelReader<NodeAddress> RebirthRequests => _rebirthRequests.Reader;

    /// <summary>Bao nhiêu node hiện đang được cho là đã chết.</summary>
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

    /// <summary>Bảng alias đang có hiệu lực cho publisher của topic này.</summary>
    /// <param name="topic">Topic của data message sắp được decode.</param>
    /// <returns>Bảng mà birth của nó đã cài đặt, hoặc <see cref="MetricAliasTable.Empty"/>.</returns>
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

    /// <summary>Áp dụng một birth: mở hoặc thay thế session và cài đặt các alias của nó.</summary>
    /// <param name="topic">Topic <c>NBIRTH</c> hoặc <c>DBIRTH</c>.</param>
    /// <param name="birth">Birth đã decode.</param>
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

            if (topic.MessageType == SparkplugMessageType.NodeBirth && node.IsNewSession(birth.BirthDeathSequence))
            {
                // Một node birth với bdSeq MỚI kết thúc hẳn session trước đó. Giữ lại các bảng
                // device cũ "phòng khi cần" chính là bug mà việc đánh số alias tạo điều kiện cho:
                // session mới hoàn toàn có thể gán 1 cho một metric khác, và mọi device không khớp
                // sẽ file reading của nó dưới ý nghĩa cũ mà không hề có một lỗi nào được nêu ra.
                //
                // So sánh bdSeq không phải là một sự tinh chỉnh, nó là toàn bộ tính đúng đắn của
                // nhánh này. Reset trên MỌI NBIRTH trông có vẻ tương đương nhưng không phải vậy:
                // MQTT là at-least-once, nên một NBIRTH được gửi lại đến với cùng bdSeq, xóa sạch
                // một bảng alias hợp lệ, và mọi message chỉ-mang-alias sau đó trở nên không đọc
                // được. Đã đo được hơn 10.000 message bị từ chối trong một run ngắn trước khi phép
                // so sánh này tồn tại.
                node.OpenSession(birth.BirthDeathSequence);
            }

            node.InstallAliases(topic.DeviceCode, birth.Aliases);
            node.RecordReadings(topic.DeviceCode, birth.Readings);
            ApplySequence(address, node, birth.Sequence);
        }
    }

    /// <summary>Áp dụng một data message: làm mới các giá trị và kiểm tra sequence.</summary>
    /// <param name="topic">Topic <c>NDATA</c> hoặc <c>DDATA</c>.</param>
    /// <param name="readings">Các reading đã decode.</param>
    /// <param name="sequence"><c>seq</c> của payload, khi nó có mang theo một giá trị.</param>
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

    /// <summary>Áp dụng một death: đánh dấu mọi metric của node là stale, và không xóa gì cả.</summary>
    /// <param name="topic">Topic <c>NDEATH</c>.</param>
    /// <param name="death">Last will đã decode.</param>
    /// <returns><see langword="true"/> khi death này đã kết thúc session đang có hiệu lực.</returns>
    /// <remarks>
    /// Việc kiểm tra <c>bdSeq</c> chính là toàn bộ lý do một death mang theo nó. Một broker giữ lại
    /// một will trong khi node kết nối lại sẽ deliver death của session 6 sau birth của session 7,
    /// và một gateway không so sánh sẽ đánh dấu một node là chết trong khi đúng lúc đó nó lại đang
    /// publish.
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
                // Một death cho một node mà gateway này chưa từng thấy sinh ra. Được ghi lại thay
                // vì bị drop: node thực sự đang không báo cáo, và "unknown" sẽ ngụ ý rằng ta không
                // có ý kiến gì.
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

    /// <summary>Yêu cầu một node khai báo lại chính nó, tối đa một lần cho mỗi gap được phát hiện.</summary>
    /// <param name="topic">Bất kỳ topic nào của node cần yêu cầu.</param>
    /// <remarks>
    /// Cũng là câu trả lời đúng cho một alias mà bảng không thể resolve: cả hai đều mang cùng một ý
    /// nghĩa — bức tranh session của gateway này đang chậm hơn so với node — và cả hai chỉ có thể
    /// sửa được bằng cách để node nói lại toàn bộ mọi thứ.
    /// </remarks>
    public void RequestRebirth(SparkplugTopic topic)
    {
        ArgumentNullException.ThrowIfNull(topic);

        RequestRebirth(NodeAddress.From(topic));
    }

    /// <summary>Những gì gateway tin là đúng về một node ngay lúc này.</summary>
    /// <param name="address">Node cần mô tả.</param>
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

    /// <summary>Mọi node mà gateway này có ý kiến về nó.</summary>
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
        // seq chỉ có một byte trên wire và cuộn vòng (wrap) ở 256, nên "đi lùi" không bao giờ là một
        // cách đọc hợp lệ cho một số nhỏ hơn — chỉ có "liệu số đếm có tiến thêm đúng một" mới là hợp lệ.
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

            // Dữ liệu đến nghĩa là node đang nói chuyện, bất kể ta tin điều gì một lúc trước. Một
            // gateway vẫn giữ trạng thái Stale trong khi reading vẫn đang chảy vào sẽ khiến một
            // control room cứ mãi truy tìm một lỗi mạng đã tự khắc phục xong rồi.
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
                // Protocol metric mô tả session, và session đã có sẵn một nơi ở trên object này rồi.
                // Để chúng lại trong bức tranh metric sẽ đặt "bdSeq is STALE" ngay trước mặt một
                // operator, điều đúng nhưng vô dụng.
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

            // Giá trị cuối cùng và timestamp của nó vẫn tồn tại, và đó chính là toàn bộ ý nghĩa.
            // "3,82 V lúc 09:14, và không còn đáng tin kể từ đó" là một phát biểu khác hẳn "không có
            // dữ liệu" và khác "điện áp bằng không", và chỉ có phát biểu đầu tiên mới đưa đúng người
            // tới đúng chỗ.
            foreach (var device in _metrics.Values)
            {
                foreach (var metricName in device.Keys.ToArray())
                {
                    device[metricName] = device[metricName] with { Liveness = NodeLiveness.Stale };
                }
            }
        }

        internal bool IsNewSession(ulong? birthDeathSequence) =>
            Liveness == NodeLiveness.Unknown
            || BirthDeathSequence is null
            || birthDeathSequence is null
            || BirthDeathSequence != birthDeathSequence;

        internal bool IsSequenceGap(ulong observed) =>
            LastSequence is { } last
            // Một số lặp lại đúng số ta vừa chấp nhận là một lần gửi lại (redelivery), không phải một
            // message bị bỏ lỡ. Cả bus lẫn broker đều là at-least-once và simulator cố tình chèn các
            // bản trùng lặp; đếm mỗi trường hợp đó thành một gap sẽ yêu cầu rebirth trên mọi bản
            // trùng lặp và chôn vùi các gap thật sự trong nhiễu.
            && observed != last
            && observed != (last + 1) % SequenceWrap;

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
