using Nvm.Contracts.Events.Quality;
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
    internal static MeasurementRow From(
        DecodedSparkplugMessage message,
        DeviceReading reading,
        DateTimeOffset recordedAt,
        TimeSpan clockDriftThreshold)
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
            ClockQualityClassifier.Classify(key.DeviceTimestamp, message.GatewayTimestamp, clockDriftThreshold),
            value.Kind,
            value.Real,
            value.Integer,
            value.Boolean,
            value.Text);
    }

    // The event's EventId is the row's SourceEventId, unchanged. That single assignment is what
    // joins device deduplication to command deduplication (scope.md §7.2); deriving a fresh id here
    // would leave both mechanisms working and keying on different values, which is R-M1-6.
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
