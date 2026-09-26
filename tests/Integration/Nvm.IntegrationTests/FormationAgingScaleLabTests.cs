using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nvm.CommandStore;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.EventStore;
using Nvm.Kernel;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Material.Commands;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Hosting;
using Nvm.ProductionExecution.Ports;
using Nvm.Quality.Hosting;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;

namespace Nvm.IntegrationTests;

/// <summary>Lab M7: 30.000 cell cùng ở trạng thái Aging. Số đo ghi vào ADR-015 và benchmarks.</summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class FormationAgingScaleLabTests(SqlCommandStoreFixture sql, ITestOutputHelper output)
    : IClassFixture<SqlCommandStoreFixture>
{
    private const int Cells = 30_000;
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThirtyThousandAgingCells_PollStaysCheap_AndDueTimeoutsDrain()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M7 aging scale lab (ADR-015).");
        await EventSchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await ProductionExecutionSchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await QualitySchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await TraceabilityFixtureSeed.PrepareAsync(sql.ConnectionString, Ct);
        var seed = Stopwatch.StartNew();
        await SqlEventBulkLoader.LoadAsync(sql.ConnectionString, "NV1", Streams(), cancellationToken: Ct);
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            using var state = new SqlCommand("""
                INSERT INTO execution.FormationAging (SiteId, SerialNumber, State, Version, TrayId, Channel, EquipmentPath,
                    FormationDueAt, CapacityAh, Ocv1Millivolt, RackId, Level, AgingChannel, AgingDueAt, UpdatedAt)
                SELECT 'NV1', SUBSTRING(StreamId, 11, 16), 'Aging', 3, 'TRAY-LAB', 1, 'NOVAVOLT/NV1/FORMATION/F1/FORM-01',
                    @formationDue, 51.2, 4150, 'R-' + RIGHT('00' + CONVERT(varchar(3), (ROW_NUMBER() OVER (ORDER BY StreamId) - 1) / 1000), 2),
                    ((ROW_NUMBER() OVER (ORDER BY StreamId) - 1) / 100) % 10 + 1,
                    (ROW_NUMBER() OVER (ORDER BY StreamId) - 1) % 100 + 1,
                    DATEADD(minute, (ROW_NUMBER() OVER (ORDER BY StreamId) - 1) % 600, @agingDue), @formationDue
                FROM es.Streams WHERE SiteId = 'NV1' AND StreamType = 'formation-aging';
                INSERT INTO execution.ProcessTimeouts (SiteId, SerialNumber, Kind, DueAt)
                SELECT SiteId, SerialNumber, 'AgingDue', AgingDueAt FROM execution.FormationAging WHERE State = 'Aging';
                """, connection) { CommandTimeout = 600 };
            state.Parameters.AddWithValue("@formationDue", T0.AddHours(36));
            state.Parameters.AddWithValue("@agingDue", T0.AddDays(10));
            await state.ExecuteNonQueryAsync(Ct);
        }
        seed.Stop();
        output.WriteLine($"SEED aging_cells={Cells} seconds={seed.Elapsed.TotalSeconds:F1}");

        var clock = new FakeTimeProvider(T0.AddDays(1));
        await using var host = Build(clock);
        var source = host.GetRequiredService<IDueTimeoutSource>();
        var polls = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            var started = Stopwatch.GetTimestamp();
            (await source.ReadDueAsync(clock.GetUtcNow(), 500, Ct)).Count.ShouldBe(0);
            polls.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        polls.Sort();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"POLL none_due p50_ms={polls[49]:F2} p95_ms={polls[94]:F2} max_ms={polls[^1]:F2}"));

        var queries = host.GetRequiredService<AgingWarehouseQueries>();
        var rack = Stopwatch.StartNew();
        var level = await queries.AtRackLevelAsync("NV1", "R-12", 3, Ct);
        rack.Stop();
        level.Count.ShouldBe(100);
        output.WriteLine($"RACK_QUERY rows={level.Count} ms={rack.Elapsed.TotalMilliseconds:F2}");

        // Tới hạn toàn bộ: worker xả 30.000 timeout trong khi một operator vẫn ghi command mới.
        clock.Advance(TimeSpan.FromDays(10));
        var worker = new FormationTimeoutWorker(host.GetRequiredService<IServiceScopeFactory>(), source, clock,
            NullLogger<FormationTimeoutWorker>.Instance);
        var commandLatencies = new List<double>();
        using var stop = new CancellationTokenSource();
        var operatorLoad = Task.Run(async () =>
        {
            var index = 0;
            while (!stop.IsCancellationRequested)
            {
                var serial = string.Create(CultureInfo.InvariantCulture, $"NV1CL16250B{++index:D5}");
                var started = Stopwatch.GetTimestamp();
                await using var scope = host.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>().DispatchAsync<UnitCommandResult>(
                    new SerializeUnitCommand("NV1", "op", "lab-" + serial, serial, clock.GetUtcNow(),
                        TraceabilityFixtureSeed.ProductCode, "WO-LAB", TraceabilityFixtureSeed.RoutingVersion), Ct);
                result.Accepted.ShouldBeTrue();
                commandLatencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                await Task.Delay(20, Ct);
            }
        }, Ct);
        var drain = Stopwatch.StartNew();
        var fired = 0;
        int batch;
        while ((batch = await worker.RunOnceAsync(500, cancellationToken: Ct)) > 0)
        { fired += batch; }
        drain.Stop();
        await stop.CancelAsync();
        await operatorLoad;
        commandLatencies.Sort();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"DRAIN fired={fired} seconds={drain.Elapsed.TotalSeconds:F1} per_second={fired / drain.Elapsed.TotalSeconds:F0} " +
            $"concurrent_commands={commandLatencies.Count} p95_ms={commandLatencies[(int)Math.Ceiling(commandLatencies.Count * 0.95) - 1]:F1}"));
        fired.ShouldBe(Cells);
        (await sql.ScalarAsync("SELECT CONVERT(varchar(10), COUNT(*)) FROM execution.FormationAging WHERE State = 'AwaitingMeasurement';",
            _ => { }, Ct)).ShouldBe(Cells.ToString(CultureInfo.InvariantCulture));
    }

    private static IEnumerable<BulkStreamEvent> Streams()
    {
        for (var i = 0; i < Cells; i++)
        {
            var serial = string.Create(CultureInfo.InvariantCulture, $"NV1CL1624{4 + i / 10_000}A{i % 10_000 + 1:D5}");
            var stream = "formation:" + serial;
            yield return Event(stream, new FormationRunStarted(Guid.NewGuid(), T0, T0, "NV1", serial, "TRAY-LAB", 1,
                "NOVAVOLT/NV1/FORMATION/F1/FORM-01", T0.AddHours(36), "seed"));
            yield return Event(stream, new FormationRunCompleted(Guid.NewGuid(), T0, T0, "NV1", serial, 51.2m, null, "seed"));
            yield return Event(stream, new AgingStarted(Guid.NewGuid(), T0, T0, "NV1", serial, 4150m, "TRAY-LAB", "R-00", 1, 1,
                T0.AddDays(10), "seed"));
        }
    }

    private static BulkStreamEvent Event(string stream, Nvm.Contracts.Events.IDomainEvent value) =>
        new(stream, "formation-aging", DomainEventRecord.Create(value, "urn:lab", "NV1:" + stream, T0));

    private ServiceProvider Build(TimeProvider clock)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NVM_COMMANDS:ConnectionString"] = sql.ConnectionString,
            ["NVM_FORMATION:TimeoutWorker"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton(clock);
        services.AddNvmKernel(typeof(RecordRollCoatedCommand).Assembly, typeof(SerializeUnitCommand).Assembly,
            typeof(ConsumeMaterialCommand).Assembly);
        services.AddNvmCommandStore(configuration, new LabEnvironment());
        services.AddNvmTraceability(configuration);
        services.AddNvmQuality();
        services.AddNvmProductionExecutionAdapters(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class LabEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Nvm.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
