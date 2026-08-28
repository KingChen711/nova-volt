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

    /// <summary>Builds the same row from a CSV line an old machine dropped on a share.</summary>
    /// <remarks>
    /// <para>
    /// Goes through <c>DeviceReadingIdentity.NaturalKey</c> — the identical call the Sparkplug path
    /// makes — and that is the whole design of C15. Two adapters with two key definitions would drift
    /// within months, and the symptom would be one measurement stored twice for exactly the machines
    /// that report through both routes: a yield number wrong on some lines and right on others, which
    /// is close to unfindable.
    /// </para>
    /// <para>
    /// The clock quality is always <see cref="ClockQuality.Unknown"/>. The CSV carries a measurement
    /// time, so the natural key works — but there is no device clock to compare against the gateway's,
    /// so calling it Good would be a claim nobody made. This is the real source of the Unknown case
    /// that the Sparkplug path cannot produce.
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
