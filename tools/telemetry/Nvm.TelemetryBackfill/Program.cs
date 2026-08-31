using System.Globalization;
using Nvm.TelemetryBackfill;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

try
{
    var options = TelemetryBackfillOptions.FromEnvironment();
    var seedDirectory = Path.Combine(AppContext.BaseDirectory, options.SeedDirectory);
    var channels = BackfillTopology.Load(seedDirectory, options.LinePath, options.ChannelCount);
    var recordedAt = TimeProvider.System.GetUtcNow();
    var spec = new TelemetryBackfillSpec(
        options.LinePath,
        channels,
        options.EndAt - options.Duration,
        options.EndAt,
        options.SamplePeriod,
        options.DriftedDeviceRate,
        options.ClockDrift,
        recordedAt);

    Console.WriteLine(
        $"Telemetry backfill: channels={channels.Length} start={spec.StartAt:O} end={spec.EndAt:O} "
        + $"sample_period={spec.SamplePeriod} drifted_rate={spec.DriftedDeviceRate:0.####} "
        + $"batch_size={options.BatchSize}.");
    Console.WriteLine(
        $"Topology: {channels.Count(path => path.Value.Contains("/FORM-01/", StringComparison.Ordinal))} "
        + $"of {channels.Length} selected channels belong to FORM-01; the 1,000-channel catalogue is "
        + "10 cyclers x 100 channels.");

    var store = new TelemetryBackfillStore(options.ConnectionString);
    var result = await store.WriteAsync(
        TelemetryBackfillGenerator.Generate(spec),
        options.BatchSize,
        Report,
        CancellationToken.None);

    foreach (var signal in result.SignalCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"NVM_BACKFILL_SIGNAL signal={signal.Key} rows={signal.Value}");
    }

    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"NVM_BACKFILL_RESULT attempted={result.Attempted} inserted={result.Inserted} "
            + $"duplicates={result.Duplicates} claims_verified={result.VerifiedClaims} "
            + $"telemetry_verified={result.VerifiedTelemetry} good={result.Good} drifted={result.Drifted} "
            + $"claim_copy_seconds={result.ClaimCopyElapsed.TotalSeconds:F6} "
            + $"claim_rows_per_second={result.ClaimRowsPerSecond:F3} "
            + $"telemetry_copy_seconds={result.TelemetryCopyElapsed.TotalSeconds:F6} "
            + $"telemetry_rows_per_second={result.TelemetryRowsPerSecond:F3} "
            + $"elapsed_seconds={result.Elapsed.TotalSeconds:F6} rows_per_second={result.RowsPerSecond:F3}"));

    return result.VerifiedClaims == result.Attempted
        && result.VerifiedTelemetry == result.Attempted
        && result.Inserted + result.Duplicates == result.Attempted
            ? 0
            : 1;
}
catch (Exception failure)
{
    await Console.Error.WriteLineAsync($"Telemetry backfill failed: {failure.Message}");
    return 2;
}

static void Report(TelemetryBackfillProgress progress)
{
    var kind = progress.IsWholeMillion ? "million" : "partial";
    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"NVM_BACKFILL_PROGRESS kind={kind} attempted_through={progress.AttemptedThrough} "
            + $"interval_rows={progress.IntervalRows} interval_inserted={progress.IntervalInserted} "
            + $"claim_copy_seconds={progress.ClaimCopyElapsed.TotalSeconds:F6} "
            + $"claim_rows_per_second={progress.ClaimRowsPerSecond:F3} "
            + $"telemetry_copy_seconds={progress.TelemetryCopyElapsed.TotalSeconds:F6} "
            + $"telemetry_rows_per_second={progress.TelemetryRowsPerSecond:F3} "
            + $"elapsed_seconds={progress.Elapsed.TotalSeconds:F6} "
            + $"rows_per_second={progress.RowsPerSecond:F3}"));
}
