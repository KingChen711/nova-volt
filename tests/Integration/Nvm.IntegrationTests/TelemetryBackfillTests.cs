using System.Collections.Immutable;
using Npgsql;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.TelemetryBackfill;

namespace Nvm.IntegrationTests;

/// <summary>C08 — direct binary COPY preserves the global claim and simulator data shape.</summary>
public sealed class TelemetryBackfillTests
{
    [Fact]
    public async Task SameDatasetTwice_StoresEveryReadingOnceWithClaimsAndBothClockQualities()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        var start = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        var line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
        var channels = Enumerable.Range(1, 8)
            .Select(index => EquipmentPath.Parse(
                $"NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-{index:0000}"))
            .ToImmutableArray();
        var spec = new TelemetryBackfillSpec(
            line,
            channels,
            start,
            start.AddHours(1),
            TimeSpan.FromMinutes(1),
            driftedDeviceRate: 0.5,
            clockDrift: TimeSpan.FromHours(2),
            recordedAt: start.AddDays(3));
        var store = new TelemetryBackfillStore(postgres.GetConnectionString());
        var progress = new List<TelemetryBackfillProgress>();

        var first = await store.WriteAsync(
            TelemetryBackfillGenerator.Generate(spec),
            batchSize: 257,
            progress.Add,
            CancellationToken.None);
        var second = await store.WriteAsync(
            TelemetryBackfillGenerator.Generate(spec),
            batchSize: 257,
            report: null,
            CancellationToken.None);

        first.Attempted.ShouldBeGreaterThan(0);
        first.Inserted.ShouldBe(first.Attempted);
        first.Duplicates.ShouldBe(0);
        first.VerifiedClaims.ShouldBe(first.Attempted);
        first.VerifiedTelemetry.ShouldBe(first.Attempted);
        first.Good.ShouldBeGreaterThan(0);
        first.Drifted.ShouldBeGreaterThan(0);
        progress.ShouldHaveSingleItem().IntervalRows.ShouldBe(first.Attempted);
        progress[0].ClaimCopyElapsed.ShouldBeGreaterThan(TimeSpan.Zero);
        progress[0].TelemetryCopyElapsed.ShouldBeGreaterThan(TimeSpan.Zero);

        second.Attempted.ShouldBe(first.Attempted);
        second.Inserted.ShouldBe(0);
        second.Duplicates.ShouldBe(second.Attempted);
        second.VerifiedClaims.ShouldBe(second.Attempted);
        second.VerifiedTelemetry.ShouldBe(second.Attempted);
        second.ClaimCopyElapsed.ShouldBe(TimeSpan.Zero);
        second.TelemetryCopyElapsed.ShouldBe(TimeSpan.Zero);

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text,
                   count(*) FILTER (WHERE clock_quality = 'Good')::text,
                   count(*) FILTER (WHERE clock_quality = 'Drifted')::text
            FROM ts.telemetry_measurement
            WHERE site_id = 'NV1';
            """))
            .ShouldBe(
            [
                first.Attempted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                first.Good.ToString(System.Globalization.CultureInfo.InvariantCulture),
                first.Drifted.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ]);

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*)::text
            FROM ts.telemetry_measurement AS telemetry
            LEFT JOIN ingest.processed_message AS processed USING (source_event_id)
            WHERE telemetry.site_id = 'NV1' AND processed.source_event_id IS NULL;
            """))
            .ShouldBe(["0"]);

        // Wide enough for the stable per-channel spread, narrow enough to reject random filler.
        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            """
            SELECT count(*) FILTER (
                       WHERE signal_code = 'Formation/Voltage'
                         AND real_value NOT BETWEEN 2.9 AND 4.3)::text,
                   count(*) FILTER (
                       WHERE signal_code = 'Formation/Current'
                         AND real_value NOT BETWEEN -0.41 AND 0.51)::text,
                   count(*) FILTER (
                       WHERE signal_code = 'Formation/Temperature'
                         AND real_value NOT BETWEEN 24 AND 33)::text,
                   count(*) FILTER (
                       WHERE signal_code = 'Formation/Capacity'
                         AND real_value NOT BETWEEN 0 AND 5)::text
            FROM ts.telemetry_measurement
            WHERE site_id = 'NV1';
            """))
            .ShouldBe(["0", "0", "0", "0"]);
    }
}
