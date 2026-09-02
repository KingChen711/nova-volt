using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.EdgeGateway.Sessions;

/// <summary>Một message tới từ edge node nào, độc lập với device bên dưới nó.</summary>
/// <param name="LinePath">Line mà node đang đại diện, chính là identity nhà máy sử dụng.</param>
/// <remarks>
/// <para>
/// <c>seq</c>, <c>bdSeq</c> và liveness đều là thuộc tính của <b>node</b>, không bao giờ là của một
/// device. Một counter riêng cho mỗi device sẽ khiến một gap trong đó trở nên vô nghĩa, và một
/// death khi đó sẽ chỉ có thể giết mỗi cái hộp đã nhận ra nó.
/// </para>
/// <para>
/// Dùng line path thay vì các chuỗi group và node, dù wire mang theo các chuỗi đó: đây là dạng
/// duy nhất có thể đưa ngược lại cho <see cref="SparkplugTopic.For"/> để định địa chỉ tới node,
/// và giữ cả hai sẽ để chúng trôi dạt khỏi nhau.
/// </para>
/// </remarks>
public readonly record struct NodeAddress(EquipmentPath LinePath)
{
    /// <summary>Group Sparkplug, mã hóa enterprise, site và area.</summary>
    public string GroupId => SparkplugTopic.For(LinePath, SparkplugMessageType.NodeCommand).GroupId;

    /// <summary>Id của edge node như nó xuất hiện trong một topic.</summary>
    public string EdgeNodeId => SparkplugTopic.EdgeNodePrefix + LinePath.Code;

    /// <summary>Nhà máy mà node này thuộc về (K3).</summary>
    public string SiteId => LinePath.Segments[1];

    /// <summary>Đọc node mà một topic định địa chỉ tới, dù ở cấp device hay không.</summary>
    /// <param name="topic">Bất kỳ topic Sparkplug nào.</param>
    public static NodeAddress From(SparkplugTopic topic)
    {
        ArgumentNullException.ThrowIfNull(topic);

        return new NodeAddress(topic.LinePath);
    }

    /// <summary>Topic mà một command gửi tới node này được publish lên.</summary>
    public SparkplugTopic NodeCommandTopic() =>
        SparkplugTopic.For(LinePath, SparkplugMessageType.NodeCommand);

    /// <inheritdoc />
    public override string ToString() => LinePath.Value;
}

/// <summary>Một giá trị được báo cáo có thể được tin tưởng tới mức nào ngay lúc này.</summary>
/// <remarks>
/// Ba trạng thái, vì một control room đọc một formation channel có ba câu hỏi và chỉ một trong số
/// đó là về cell. <c>0</c> đọc thành "điện áp bằng không" và kích hoạt một cảnh báo trên phần cứng
/// khỏe mạnh; <c>null</c> đọc thành "chưa đo được gì cả" và bị bỏ qua. Chỉ có <see cref="Stale"/>
/// nói lên điều thực sự đúng sau một <c>NDEATH</c>: con số cuối cùng là X tại thời điểm T, và
/// không gì kể từ đó có thể tin được. Điều đó khiến người ta đi kiểm tra mạng thay vì kiểm tra cell.
/// </remarks>
public enum NodeLiveness
{
    /// <summary>Chưa có birth nào được thấy cho node này trong suốt vòng đời của gateway này.</summary>
    Unknown,

    /// <summary>Node đã publish một birth và chưa chết kể từ đó.</summary>
    Online,

    /// <summary>Last will của node đã kích hoạt. Lịch sử của nó vẫn đứng vững; hiện tại của nó thì không.</summary>
    Stale,
}

/// <summary>Điều cuối cùng một metric đã nói, và liệu điều đó có còn đúng hay không.</summary>
/// <param name="MetricName">Metric, theo tên mà birth của nó đã khai báo.</param>
/// <param name="LastValue">Giá trị gần nhất được thấy.</param>
/// <param name="LastDeviceTimestamp">Thời điểm device nói rằng nó lấy giá trị đó.</param>
/// <param name="Liveness">Node đứng sau nó có còn sống hay không.</param>
public sealed record NodeMetricState(
    string MetricName,
    MetricValue LastValue,
    DateTimeOffset LastDeviceTimestamp,
    NodeLiveness Liveness);

/// <summary>Những gì gateway hiện đang tin là đúng về một edge node.</summary>
/// <param name="Address">Node.</param>
/// <param name="Liveness">Sống, chết, hoặc chưa từng thấy.</param>
/// <param name="BirthDeathSequence">Số session của birth đang có hiệu lực, khi nó mang theo một số.</param>
/// <param name="LastSequence"><c>seq</c> cuối cùng được chấp nhận từ node này.</param>
/// <param name="StaleSince">Thời điểm death được xử lý, hoặc null trong khi node còn sống.</param>
/// <param name="SequenceGaps">Số lần một message đến không đúng thứ tự đối với node này.</param>
/// <param name="Metrics">Mọi metric của mọi device dưới node, giá trị mới nhất được thấy sau cùng.</param>
public sealed record NodeSessionSnapshot(
    NodeAddress Address,
    NodeLiveness Liveness,
    ulong? BirthDeathSequence,
    ulong? LastSequence,
    DateTimeOffset? StaleSince,
    long SequenceGaps,
    ImmutableArray<NodeMetricState> Metrics);
