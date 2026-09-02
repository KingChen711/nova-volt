using Nvm.Ingestion;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.TelemetryBackfill;

/// <summary>Một reading của simulator có hình dạng khớp chính xác với hai ingestion table mong đợi.</summary>
public sealed record BackfillRow(
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
    string ClockQuality,
    string ValueKind,
    double? RealValue,
    long? IntegerValue,
    bool? BooleanValue,
    string? TextValue)
{
    /// <summary>Map một reading đã decode mà không suy ra một identity thứ hai.</summary>
    public static BackfillRow From(
        EquipmentPath equipmentPath,
        DeviceReading reading,
        DateTimeOffset gatewayTimestamp,
        DateTimeOffset recordedAt)
    {
        var key = reading.NaturalKey(equipmentPath);
        (string Kind, double? Real, long? Integer, bool? Boolean, string? Text) stored = reading.Value switch
        {
            MetricValue.Real real => ("real", real.Value, null, null, null),
            MetricValue.Integral integer => ("integer", null, integer.Value, null, null),
            MetricValue.Flag flag => ("boolean", null, null, flag.Value, null),
            MetricValue.Text text => ("text", null, null, null, text.Value),
            MetricValue.Absent => ("absent", null, null, null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(reading), reading.Value, "Unknown metric value case."),
        };

        return new BackfillRow(
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
            ClockQualityClassifier.Classify(
                key.DeviceTimestamp,
                gatewayTimestamp,
                ClockQualityClassifier.DefaultThreshold).ToColumnValue(),
            stored.Kind,
            stored.Real,
            stored.Integer,
            stored.Boolean,
            stored.Text);
    }
}
