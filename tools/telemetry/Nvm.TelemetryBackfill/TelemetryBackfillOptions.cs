using System.Globalization;
using Nvm.Kernel.Identity;
using Nvm.Simulator.Formation;

namespace Nvm.TelemetryBackfill;

/// <summary>Validated command-line environment for a deterministic historical dataset.</summary>
public sealed record TelemetryBackfillOptions(
    string ConnectionString,
    string SeedDirectory,
    EquipmentPath LinePath,
    int ChannelCount,
    TimeSpan Duration,
    DateTimeOffset EndAt,
    TimeSpan SamplePeriod,
    double DriftedDeviceRate,
    TimeSpan ClockDrift,
    int BatchSize)
{
    private static readonly DateTimeOffset DefaultEndAt =
        new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Reads the variables set by <c>make telemetry-backfill</c>.</summary>
    public static TelemetryBackfillOptions FromEnvironment()
    {
        var connectionString = Required("NVM_BACKFILL_CONNECTION_STRING");
        var seedDirectory = Value("NVM_BACKFILL_SEED_DIRECTORY", "seed-load");
        var linePath = EquipmentPath.Parse(Value("NVM_BACKFILL_LINE_PATH", "NOVAVOLT/NV1/FORMATION/F1"));
        var channelCount = Integer("NVM_BACKFILL_CHANNELS", 8);
        var duration = TimeSpan.FromDays(Number("NVM_BACKFILL_DAYS", 1));
        var endAt = Timestamp("NVM_BACKFILL_END_AT", DefaultEndAt).ToUniversalTime();
        var samplePeriod = TimeSpan.FromSeconds(Number("NVM_BACKFILL_SAMPLE_PERIOD_SECONDS", 5));
        var driftedRate = Number("NVM_BACKFILL_DRIFTED_RATE", 0);
        var clockDrift = TimeSpan.FromHours(Number("NVM_BACKFILL_CLOCK_DRIFT_HOURS", 2));
        var batchSize = Integer("NVM_BACKFILL_BATCH_SIZE", 50_000);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, TimeSpan.FromDays(399));
        if (samplePeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(samplePeriod));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(driftedRate, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(driftedRate, 1);
        if (clockDrift < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(clockDrift));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        if (samplePeriod.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new InvalidOperationException(
                "The sample period must be a whole millisecond: Sparkplug device time has millisecond resolution.");
        }

        if (duration.Ticks % samplePeriod.Ticks != 0)
        {
            throw new InvalidOperationException("The requested duration must contain a whole number of samples.");
        }

        if (FormationProfile.Default.CycleDuration.Ticks % samplePeriod.Ticks != 0)
        {
            throw new InvalidOperationException("The eighteen-hour formation cycle must contain a whole number of samples.");
        }

        return new TelemetryBackfillOptions(
            connectionString,
            seedDirectory,
            linePath,
            channelCount,
            duration,
            endAt,
            samplePeriod,
            driftedRate,
            clockDrift,
            batchSize);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is required.");

    private static string Value(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    private static int Integer(string name, int fallback) =>
        int.Parse(Value(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

    private static double Number(string name, double fallback) =>
        double.Parse(Value(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

    private static DateTimeOffset Timestamp(string name, DateTimeOffset fallback) =>
        DateTimeOffset.Parse(
            Value(name, fallback.ToString("O", CultureInfo.InvariantCulture)),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
