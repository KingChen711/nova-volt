using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion;
using Nvm.Ingestion.FileDrop;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// C15: an end-of-line tester that has never heard of MQTT still comes in through the same door.
/// The two adapters differ in how they read and in nothing else — same natural key, same claim, same
/// transaction — because two dedup definitions drift within months and the symptom is one
/// measurement stored twice for exactly the machines that report through both routes.
/// </summary>
public sealed class FileDropTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly EquipmentPath Line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");

    // Interpolation rather than string.Format: one reading per index, and the analyzers are right
    // that a format string used in a loop wants a CompositeFormat it does not need here.
    private static string Row(int index) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,,Formation/Capacity,2026-08-28T09:28:11.{index:000}Z,real,4.8{index:000}");

    [Fact]
    public async Task TheSameFileDroppedTwice_DoesNotChangeTheRowCount()
    {
        await using var harness = await FileDropHarness.StartAsync();

        var first = await harness.DropAsync("eol-export.csv", Lines(3));
        first.ShouldNotBeNull();
        first.Inserted.ShouldBe(3);
        (await harness.CountTelemetryAsync()).ShouldBe(3);

        // Byte for byte the same export. The natural key is a function of what was measured, not of
        // when the file arrived, so nothing new is stored.
        var second = await harness.DropAsync("eol-export.csv", Lines(3));
        second.ShouldNotBeNull();
        second.Inserted.ShouldBe(0);
        second.Duplicates.ShouldBe(3);
        (await harness.CountTelemetryAsync()).ShouldBe(3);

        harness.Processed().Count.ShouldBe(1);
    }

    [Fact]
    public async Task OneBadLineIsRejectedAlone_AndTheOtherNinetyNineAreStored()
    {
        await using var harness = await FileDropHarness.StartAsync();

        var lines = Lines(100).ToList();
        lines[50] = lines[50].Replace(",real,4.", ",real,not-a-number-4.", StringComparison.Ordinal);

        var result = await harness.DropAsync("eol-export.csv", lines);

        result.ShouldNotBeNull();
        result.Inserted.ShouldBe(99);
        (await harness.CountTelemetryAsync()).ShouldBe(99);

        // The file itself succeeded; only the line failed.
        harness.Processed().ShouldHaveSingleItem().ShouldBe("eol-export.csv");

        var rejected = harness.Rejected();
        rejected.ShouldContain("eol-export.line-52.csv");
        rejected.ShouldContain("eol-export.line-52.csv.error");

        var reason = await harness.ReadRejectedAsync("eol-export.line-52.csv.error");
        reason.ShouldContain("not-a-number");
        reason.ShouldContain("The other lines of the file were stored");

        // The header travels with the rejected line: a row of commas alone is something an operator
        // has to decode by counting.
        (await harness.ReadRejectedAsync("eol-export.line-52.csv"))
            .ShouldStartWith(CsvMeasurementReader.Header);
    }

    [Fact]
    public async Task AFileWithNoHeader_IsRejectedWholeAndStoresNothing()
    {
        await using var harness = await FileDropHarness.StartAsync();

        var result = await harness.DropAsync("headerless.csv", [Row(1)], writeHeader: false);

        result.ShouldBeNull();
        (await harness.CountTelemetryAsync()).ShouldBe(0);
        harness.Processed().ShouldBeEmpty();
        harness.Rejected().ShouldContain("headerless.csv");
        (await harness.ReadRejectedAsync("headerless.csv.error")).ShouldContain("header");
    }

    [Fact]
    public async Task FileDropRows_AreStoredWithClockQualityUnknown()
    {
        // The case the Sparkplug path cannot produce. C02 refuses a metric with no timestamp because
        // device_timestamp is in the natural key; here the CSV carries a measurement time, so the key
        // works — but no device clock was ever involved, so calling it Good would be a claim nobody
        // made. This is the third value of scope.md §7.3 arriving from its real source.
        await using var harness = await FileDropHarness.StartAsync();

        await harness.DropAsync("eol-export.csv", Lines(2));

        var qualities = await harness.ReadClockQualitiesAsync();

        qualities.ShouldBe(["Unknown", "Unknown"]);
    }

    private static List<string> Lines(int count) =>
        [.. Enumerable.Range(1, count).Select(index => Row(index))];

    private sealed class FileDropHarness : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _postgres;
        private readonly NpgsqlDataSource _dataSource;
        private readonly FileDropOptions _options;
        private readonly FileDropProcessor _processor;
        private readonly string _root;

        private FileDropHarness(
            PostgreSqlContainer postgres,
            NpgsqlDataSource dataSource,
            FileDropOptions options,
            FileDropProcessor processor,
            string root)
        {
            _postgres = postgres;
            _dataSource = dataSource;
            _options = options;
            _processor = processor;
            _root = root;
        }

        internal static async Task<FileDropHarness> StartAsync()
        {
            var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
                .WithDatabase("novavolt_integration")
                .WithUsername("nvm")
                .WithPassword("nvm_integration_only")
                .Build();

            await postgres.StartAsync(CancellationToken.None);
            IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

            var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
            var root = Path.Combine(Path.GetTempPath(), "nvm-file-drop-tests", Guid.NewGuid().ToString("N"));

            var options = new FileDropOptions
            {
                Enabled = true,
                InboxPath = Path.Combine(root, "inbox"),
                ProcessedPath = Path.Combine(root, "processed"),
                RejectedPath = Path.Combine(root, "rejected"),
                SettleTime = TimeSpan.Zero,
            };
            options.Validate();

            var clock = new FakeTimeProvider(ReadAt);
            var processor = new FileDropProcessor(
                new CsvMeasurementReader(new OneChannelDirectory()),
                new PostgresMeasurementIngestor(dataSource, clock, new IngestionMetrics()),
                options,
                clock,
                NullLogger<FileDropProcessor>.Instance);

            processor.EnsureDirectories();

            return new FileDropHarness(postgres, dataSource, options, processor, root);
        }

        internal async Task<IngestionResult?> DropAsync(
            string fileName,
            IReadOnlyList<string> rows,
            bool writeHeader = true)
        {
            var path = Path.Combine(_options.InboxPath, fileName);
            var lines = writeHeader ? [CsvMeasurementReader.Header, .. rows] : rows;

            await File.WriteAllLinesAsync(path, lines, TestContext.Current.CancellationToken);

            return await _processor.ProcessAsync(path, TestContext.Current.CancellationToken);
        }

        internal IReadOnlyList<string> Processed() =>
            [.. Directory.EnumerateFiles(_options.ProcessedPath).Select(Path.GetFileName).OfType<string>()];

        internal IReadOnlyList<string> Rejected() =>
            [.. Directory.EnumerateFiles(_options.RejectedPath).Select(Path.GetFileName).OfType<string>()];

        internal Task<string> ReadRejectedAsync(string fileName) =>
            File.ReadAllTextAsync(
                Path.Combine(_options.RejectedPath, fileName),
                TestContext.Current.CancellationToken);

        internal async Task<long> CountTelemetryAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM ts.telemetry_measurement;",
                connection);

            return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        internal async Task<IReadOnlyList<string>> ReadClockQualitiesAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT clock_quality FROM ts.telemetry_measurement ORDER BY device_timestamp;",
                connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            var qualities = new List<string>();

            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                qualities.Add(reader.GetString(0));
            }

            return qualities;
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync();
            await _postgres.DisposeAsync();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class OneChannelDirectory : IEquipmentDirectory
    {
        public bool Contains(EquipmentPath path) => path == Line || path == Channel;

        public EquipmentPath? FindDevice(EquipmentPath line, string deviceCode) =>
            line == Line && string.Equals(deviceCode, Channel.Code, StringComparison.Ordinal)
                ? Channel
                : null;
    }
}
