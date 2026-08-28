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
    string ValueKind,
    double? RealValue,
    long? IntegerValue,
    bool? BooleanValue,
    string? TextValue)
{
    internal static MeasurementRow From(
        DecodedSparkplugMessage message,
        DeviceReading reading,
        DateTimeOffset recordedAt)
    {
        var key = reading.NaturalKey(message.EquipmentPath);

        (string Kind, double? Real, long? Integer, bool? Boolean, string? Text) value = reading.Value switch
        {
            MetricValue.Real real => ("real", (double?)real.Value, null, null, null),
            MetricValue.Integral integer => ("integer", null, (long?)integer.Value, null, null),
            MetricValue.Flag flag => ("boolean", null, null, (bool?)flag.Value, null),
            MetricValue.Text text => ("text", null, null, null, text.Value),
            MetricValue.Absent => ("absent", null, null, null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(reading), reading.Value, "Unknown metric value case."),
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
            message.GatewayTimestamp,
            recordedAt,
            value.Kind,
            value.Real,
            value.Integer,
            value.Boolean,
            value.Text);
    }
}
