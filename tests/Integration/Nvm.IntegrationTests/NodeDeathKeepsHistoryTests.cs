using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.EdgeGateway;
using Nvm.EdgeGateway.Decoding;
using Nvm.EdgeGateway.Sessions;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// D4, both halves at once. The interesting half is not that a node goes STALE — it is that the
/// readings it already produced are still there afterwards. A formation cell that loses the network
/// six hours into an eighteen-hour cycle still has six hours of legally required record behind it,
/// and K4 says nothing may delete that.
/// </summary>
public sealed class NodeDeathKeepsHistoryTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 8, 28, 9, 30, 0, TimeSpan.Zero);
    private static readonly EquipmentPath Line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");

    private const string NodeBirthTopic = "spBv1.0/NOVAVOLT-NV1-FORMATION/NBIRTH/EDGE-F1";
    private const string NodeDeathTopic = "spBv1.0/NOVAVOLT-NV1-FORMATION/NDEATH/EDGE-F1";
    private const string DeviceBirthTopic = "spBv1.0/NOVAVOLT-NV1-FORMATION/DBIRTH/EDGE-F1/FORM-01-CH-0142";

    [Fact]
    public async Task NodeDeath_TurnsEveryMetricStaleAndLeavesTelemetryUntouched()
    {
        await using var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
            .WithDatabase("novavolt_integration")
            .WithUsername("nvm")
            .WithPassword("nvm_integration_only")
            .Build();

        await postgres.StartAsync(CancellationToken.None);
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        var clock = new FakeTimeProvider(ReceivedAt);
        var counters = new GatewayCounters();
        var tracker = new NodeSessionTracker(counters, clock);
        var decoder = new SparkplugMessageDecoder(tracker, new LineDirectory(), clock);
        var ingestor = new PostgresMeasurementIngestor(dataSource, clock, new IngestionMetrics());

        decoder.Decode(NodeBirthTopic, SparkplugPayloads.NodeBirth(birthDeathSequence: 6));
        var declared = decoder.Decode(
            DeviceBirthTopic,
            SparkplugPayloads.BirthDeclaring(seq: 1, ("Formation/Voltage", 1), ("Formation/Current", 2)));

        declared.ShouldNotBeNull();
        var stored = await ingestor.IngestAsync([declared], TestContext.Current.CancellationToken);
        stored.Inserted.ShouldBe(2);

        var before = await CountTelemetryAsync(dataSource, TestContext.Current.CancellationToken);
        before.ShouldBe(2);

        // The broker publishes the will. Nothing else happens.
        clock.Advance(TimeSpan.FromMinutes(3));
        decoder.Decode(NodeDeathTopic, SparkplugPayloads.NodeDeath(birthDeathSequence: 6)).ShouldBeNull();

        var snapshot = tracker.Snapshot(new NodeAddress(Line));
        snapshot.Liveness.ShouldBe(NodeLiveness.Stale);
        snapshot.StaleSince.ShouldBe(ReceivedAt.AddMinutes(3));
        snapshot.Metrics.Length.ShouldBe(2);
        snapshot.Metrics.ShouldAllBe(metric => metric.Liveness == NodeLiveness.Stale);

        (await CountTelemetryAsync(dataSource, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    private static async Task<long> CountTelemetryAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM ts.telemetry_measurement;",
            connection);

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private sealed class LineDirectory : IEquipmentDirectory
    {
        public bool Contains(EquipmentPath path) => path == Line;

        public EquipmentPath? FindDevice(EquipmentPath line, string deviceCode) =>
            line == Line && string.Equals(deviceCode, Channel.Code, StringComparison.Ordinal)
                ? Channel
                : null;
    }

    /// <summary>The few payload shapes this test needs, built here rather than captured.</summary>
    /// <remarks>
    /// Duplicated from the unit-test helper on purpose: the two projects answer different questions
    /// and sharing a fixture builder between them would make a change made for one silently rewrite
    /// the other's evidence.
    /// </remarks>
    private static class SparkplugPayloads
    {
        internal static byte[] NodeBirth(ulong birthDeathSequence) =>
            SparkplugPayload.EncodeBirth(
                [Reading(SparkplugPayload.BirthDeathSequenceMetric, (long)birthDeathSequence)],
                sequence: 0,
                ReceivedAt);

        internal static byte[] NodeDeath(ulong birthDeathSequence) =>
            SparkplugPayload.EncodeData(
                [Reading(SparkplugPayload.BirthDeathSequenceMetric, (long)birthDeathSequence)],
                sequence: 0,
                ReceivedAt);

        internal static byte[] BirthDeclaring(ulong seq, params (string Name, ulong Alias)[] metrics) =>
            SparkplugPayload.EncodeBirth(
                [.. metrics.Select(metric => new DeviceReading(
                    metric.Name,
                    metric.Alias,
                    new MetricValue.Real(3.82),
                    ReceivedAt))],
                seq,
                ReceivedAt);

        private static DeviceReading Reading(string name, long value) =>
            new(name, Alias: null, new MetricValue.Integral(value), ReceivedAt);
    }
}
