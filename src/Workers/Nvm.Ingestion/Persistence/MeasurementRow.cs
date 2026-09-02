using Nvm.Contracts.Events.Quality;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.Ingestion.Persistence;

internal sealed record MeasurementRow(
    Guid SourceEventId,
    string SiteId,
    string NaturalKey,
    string EquipmentId,
    string? UnitId,
    string StepCode,
    string SignalCode,
    DateTimeOffset DeviceTimestamp,
    DateTimeOffset GatewayTimestamp,
    DateTimeOffset RecordedAt,
    ClockQuality ClockQuality,
    string ValueKind,
    double? RealValue,
    long? IntegerValue,
    bool? BooleanValue,
    string? TextValue)
{
    internal static MeasurementRow FromSparkplug(
        DecodedSparkplugMessage message,
        DeviceReading reading,
        DateTimeOffset recordedAt,
        TimeSpan clockDriftThreshold) =>
        Build(
            reading.NaturalKey(message.EquipmentPath),
            reading.Value,
            message.GatewayTimestamp,
            recordedAt,
            ClockQualityClassifier.Classify(reading.DeviceTimestamp, message.GatewayTimestamp, clockDriftThreshold));

    /// <summary>Xây cùng một dòng từ một dòng CSV mà một máy cũ drop lên một share.</summary>
    /// <remarks>
    /// <para>
    /// Đi qua <c>DeviceReadingIdentity.NaturalKey</c> — đúng cùng lời gọi mà đường Sparkplug thực
    /// hiện — và đó là toàn bộ thiết kế của C15. Hai adapter với hai định nghĩa key sẽ trôi dạt khỏi
    /// nhau chỉ sau vài tháng, và triệu chứng sẽ là một measurement bị lưu hai lần đúng cho những máy
    /// báo cáo qua cả hai route: một con số yield sai ở một số dòng và đúng ở những dòng khác, gần
    /// như không thể tìm ra được.
    /// </para>
    /// <para>
    /// Clock quality luôn luôn là <see cref="ClockQuality.Unknown"/>. CSV mang theo một thời điểm đo,
    /// nên natural key vẫn hoạt động — nhưng không có device clock nào để so sánh với gateway's, nên
    /// gọi nó là Good sẽ là một tuyên bố không ai đưa ra cả. Đây chính là nguồn gốc thật của trường
    /// hợp Unknown mà đường Sparkplug không bao giờ tạo ra được.
    /// </para>
    /// </remarks>
    internal static MeasurementRow FromFileDrop(
        DeviceReading reading,
        EquipmentPath equipmentPath,
        string? unitId,
        DateTimeOffset readAt,
        DateTimeOffset recordedAt) =>
        Build(
            reading.NaturalKey(equipmentPath, unitId),
            reading.Value,
            readAt,
            recordedAt,
            ClockQuality.Unknown);

    private static MeasurementRow Build(
        MeasurementNaturalKey key,
        MetricValue value,
        DateTimeOffset gatewayTimestamp,
        DateTimeOffset recordedAt,
        ClockQuality clockQuality)
    {
        (string Kind, double? Real, long? Integer, bool? Boolean, string? Text) stored = value switch
        {
            MetricValue.Real real => ("real", (double?)real.Value, null, null, null),
            MetricValue.Integral integer => ("integer", null, (long?)integer.Value, null, null),
            MetricValue.Flag flag => ("boolean", null, null, (bool?)flag.Value, null),
            MetricValue.Text text => ("text", null, null, null, text.Value),
            MetricValue.Absent => ("absent", null, null, null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown metric value case."),
        };

        return new MeasurementRow(
            key.SourceEventId.Value,
            key.SiteId,
            key.ToString(),
            key.EquipmentPath.Value,
            key.UnitId,
            key.StepCode,
            key.SignalCode,
            key.DeviceTimestamp,
            gatewayTimestamp,
            recordedAt,
            clockQuality,
            stored.Kind,
            stored.Real,
            stored.Integer,
            stored.Boolean,
            stored.Text);
    }

    // EventId của event chính là SourceEventId của dòng, không đổi. Chính phép gán đơn giản này là
    // thứ nối device deduplication với command deduplication (scope.md §7.2); tự sinh một id mới ở
    // đây sẽ khiến cả hai cơ chế vẫn hoạt động nhưng key theo hai giá trị khác nhau, đó là R-M1-6.
    internal MeasurementRecorded ToEvent() =>
        new(
            SourceEventId,
            RecordedAt,
            SiteId,
            EquipmentId,
            UnitId,
            StepCode,
            SignalCode,
            DeviceTimestamp,
            GatewayTimestamp,
            ClockQuality.ToColumnValue(),
            ValueKind,
            RealValue,
            IntegerValue,
            BooleanValue,
            TextValue);
}
