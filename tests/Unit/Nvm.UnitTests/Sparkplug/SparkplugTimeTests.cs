using System.Reflection;
using Nvm.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;
using SparkplugMetric = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Timestamp của reading đến từ đâu, và type nào được phép dùng cho nó.</summary>
public sealed class SparkplugTimeTests
{
    private const ulong PayloadMs = 1787901330500;

    [Fact]
    public void NoTypeInThisAssemblyPutsADateTimeOnADeviceReading()
    {
        // AGENTS.md K2, được check ở đây vì không analyzer nào cover nó. NVM002 từ chối DateTime trong
        // Nvm.Contracts còn assembly này không phải Nvm.Contracts, nên thiếu test này rule đúng ở mọi
        // nơi trừ đúng chỗ device clock thực sự đi vào hệ thống.
        //
        // Site DE1 có daylight saving: mỗi mùa thu một giờ xảy ra hai lần. Reading wall-clock không có
        // offset không nói được đó là lần nào, telemetry được giữ 400 ngày, và store là append-only —
        // nên ambiguity vẫn nằm trong table, không thể sửa, khi ai đó đến điều tra.
        var offenders = typeof(DeviceReading).Assembly
            .GetExportedTypes()
            .Where(type => string.Equals(type.Namespace, "Nvm.Sparkplug", StringComparison.Ordinal))
            .SelectMany(MembersMentioningDateTime)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty($"DateTime reaches a reading through: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Control_TheDateTimeWalkFindsOneBuriedInAGeneric()
    {
        // Không có nó, walk chỉ nhìn outermost type — hoặc không nhìn type nào vì namespace filter viết
        // sai — sẽ báo "no offenders" mãi mãi.
        MembersMentioningDateTime(typeof(ReadingWithForbiddenClock))
            .ShouldContain($"{nameof(ReadingWithForbiddenClock)}.{nameof(ReadingWithForbiddenClock.Samples)}");
    }

    [Fact]
    public void AMetricWithoutItsOwnTimestampInheritsThePayloadsOne()
    {
        // Hợp lệ và phổ biến: device đọc mọi thứ trong một lượt sẽ gắn timestamp cho payload và để
        // metric trống.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Datatype = (uint)DataType.Float,
            FloatValue = 3.6875f,
        };

        SparkplugPayload.DecodeData(SparkplugPayloads.Encode(PayloadMs, seq: 1, metric), MetricAliasTable.Empty)
            .Single()
            .DeviceTimestamp
            .ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds((long)PayloadMs));
    }

    [Fact]
    public void MetricsKeepTheirOwnInstantsRatherThanThePayloadsOne()
    {
        // Một message thường gom các reading ở thời điểm khác nhau — đó là lý do Sparkplug metric có
        // timestamp riêng. Gộp chúng vào timestamp của payload sẽ căn các sample vốn không đồng thời,
        // trong khi process engineering đọc chính những khoảng lệch đó.
        var readings = SparkplugPayload.DecodeData(
            SparkplugPayloads.Encode(
                PayloadMs,
                seq: 1,
                Stamped("Formation/Voltage", PayloadMs - 400),
                Stamped("Formation/Temperature", PayloadMs - 150)),
            MetricAliasTable.Empty);

        readings.Select(reading => reading.DeviceTimestamp).ShouldBe(
        [
            DateTimeOffset.FromUnixTimeMilliseconds((long)PayloadMs - 400),
            DateTimeOffset.FromUnixTimeMilliseconds((long)PayloadMs - 150),
        ]);
    }

    [Fact]
    public void AReadingWithNoTimestampAnywhereIsRefused()
    {
        // device_timestamp là phần của natural key (docs/scope.md §7.2), nên reading thiếu nó không có
        // dedup identity. Chấp nhận nó sẽ ghi measurement không bao giờ được nhận ra là duplicate của
        // chính nó — C04 không có cách phát hiện.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Datatype = (uint)DataType.Float,
            FloatValue = 3.6875f,
        };

        Should.Throw<SparkplugDecodeException>(() => SparkplugPayload.DecodeData(
            SparkplugPayloads.Encode(timestamp: null, seq: 1, metric),
            MetricAliasTable.Empty));
    }

    [Fact]
    public void AClockSoWrongItIsNotAnInstantIsRefusedAsABadMessage()
    {
        // PLC có clock hỏng không được làm ingestion dừng bởi ArgumentOutOfRangeException ném ở đâu đó
        // trong BCL. Nó là bad message và nhận câu trả lời dành cho bad message.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Datatype = (uint)DataType.Float,
            Timestamp = ulong.MaxValue,
            FloatValue = 3.6875f,
        };

        Should.Throw<SparkplugDecodeException>(() =>
            SparkplugPayload.DecodeData(SparkplugPayloads.Carrying(metric), MetricAliasTable.Empty));
    }

    private static SparkplugMetric Stamped(string name, ulong milliseconds) =>
        new()
        {
            Name = name,
            Datatype = (uint)DataType.Float,
            Timestamp = milliseconds,
            FloatValue = 1f,
        };

    private static IEnumerable<string> MembersMentioningDateTime(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => Mentions(property.PropertyType))
            .Select(property => $"{type.Name}.{property.Name}");

    private static bool Mentions(Type type) =>
        type == typeof(DateTime)
        || (type.IsArray && Mentions(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(Mentions));

    /// <summary>Reading sai có chủ ý, để chứng minh walk ở trên có thể fail.</summary>
    public sealed record ReadingWithForbiddenClock(
        string MetricName,
        DateTimeOffset DeviceTimestamp,
        IReadOnlyList<DateTime> Samples);
}
