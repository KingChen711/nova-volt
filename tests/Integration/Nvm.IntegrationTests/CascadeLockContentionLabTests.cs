using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.CommandStore;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Events.Traceability;
using Nvm.EventStore;
using Nvm.Kernel.EventSourcing;
using Nvm.Material.Commands;
using Nvm.Projections;
using Nvm.Quality.Commands;
using Nvm.Quality.Hosting;
using Nvm.Quality.Ports;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// Lab phá hoại M9 #1: cascade trong một transaction lớn thay vì chunk 1.000 unit. Trong lúc cascade chạy, một luồng khác
/// liên tục gửi command tiêu hao cho cell KHÔNG liên quan (đọc quality facet, ghi event store) và đo độ trễ.
/// </summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class CascadeLockContentionLabTests(SqlCommandStoreFixture sql, ITestOutputHelper output)
    : IClassFixture<SqlCommandStoreFixture>
{
    private static readonly int PacksPerLot = int.TryParse(Environment.GetEnvironmentVariable("NVM_LAB_PACKS"), out var packs) ? packs : 200;
    private const int ModulesPerPack = 8;
    private const int CellsPerModule = 12;
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OneBigTransaction_BlocksUnrelatedCommands_ChunksDoNot()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M9 cascade lock-contention lab (ADR-017).");
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        await SqlEventBulkLoader.LoadAsync(sql.ConnectionString, "NV1", Dataset(), cancellationToken: Ct);
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(Ct);
        await using var data = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await ProjectionSchemaMigrator.UpgradeAsync(data, Ct);
        await new GenealogyProjection(data, new SqlGlobalEventFeed(sql.ConnectionString)).RebuildAsync("NV1", cancellationToken: Ct);

        var clock = new FakeTimeProvider(T0);
        await using var probeHost = TestCommandHost.Build(sql.ConnectionString, clock, data);
        var probes = Enumerable.Range(0, 5).Select(i => Serial('C', "L2", i)).ToArray();
        foreach (var cell in probes)
        {
            (await probeHost.DispatchAsync(new SerializeUnitCommand("NV1", "op", "birth-" + cell, cell, T0,
                TraceabilityFixtureSeed.ProductCode, "WO-LAB", TraceabilityFixtureSeed.RoutingVersion), Ct)).Accepted.ShouldBeTrue();
        }

        var order = Environment.GetEnvironmentVariable("NVM_LAB_ORDER") ?? "chunked,single";
        foreach (var run in order.Split(','))
        {
            output.WriteLine(run == "single"
                ? await RunAsync("one-transaction", "ELY-B", 1_000_000, clock, data, probeHost, probes)
                : await RunAsync("chunk-1000", "ELY-A", 1_000, clock, data, probeHost, probes));
        }
    }

    private async Task<string> RunAsync(string label, string lot, int chunkSize, FakeTimeProvider clock, NpgsqlDataSource data,
        ServiceProvider probeHost, string[] probes)
    {
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock, data,
            services => services.Replace(ServiceDescriptor.Singleton(new CascadePolicy(chunkSize))));
        var worker = new HoldCascadeWorker(host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<SqlCommandStoreOptions>(), new HoldCascadeOptions(TimeSpan.Zero), clock,
            NullLogger<HoldCascadeWorker>.Instance);
        (await host.DispatchAsync(new PlaceHoldCommand("NV1", "qa.eng", "hold-" + label, T0, "Lot", lot, null, null,
            "CONTAMINATION", null), Ct)).Accepted.ShouldBeTrue();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var latencies = new List<double>();
        var failures = 0;
        var probe = Task.Run(async () =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                var cell = probes[i % probes.Length];
                var from = 10m * i;
                var watch = Stopwatch.StartNew();
                try
                {
                    var result = await probeHost.DispatchAsync(new ConsumeMaterialCommand("NV1", "op", $"{label}-{i}", T0, cell,
                        "ROLL-PROBE-" + label, "Roll", "ANODE", 1m, "m", from, from + 1m, "RUN"), CancellationToken.None);
                    if (result.Accepted)
                    { latencies.Add(watch.Elapsed.TotalMilliseconds); }
                    else
                    { failures++; }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { failures++; }
                await Task.Delay(20, CancellationToken.None);
            }
        }, CancellationToken.None);
        await Task.Delay(500, Ct);   // đo nền trước khi cascade bắt đầu
        var escalationsBefore = await LockEscalationsAsync();
        var cascade = Stopwatch.StartNew();
        var chunks = await worker.RunOnceAsync(Ct);
        cascade.Stop();
        await stop.CancelAsync();
        await probe;
        var escalationDelta = await LockEscalationsAsync() - escalationsBefore;
        var (escalations, attempts) = (escalationDelta / 1_000_000, escalationDelta % 1_000_000);

        failures.ShouldBe(0);
        latencies.Sort();
        double P(double q) => latencies.Count == 0 ? double.NaN : latencies[(int)Math.Ceiling(latencies.Count * q) - 1];
        return string.Create(CultureInfo.InvariantCulture,
            $"CONTENTION {label} chunks={chunks} cascade_s={cascade.Elapsed.TotalSeconds:F1} probes_ok={latencies.Count} probe_failures={failures} p50_ms={P(0.5):F0} p95_ms={P(0.95):F0} max_ms={(latencies.Count == 0 ? double.NaN : latencies[^1]):F0} lock_escalations={escalations} escalation_attempts={attempts}");
    }

    /// <summary>
    /// Số lần (triệu × thành công + số lần thử) SQL Server nâng khóa dòng/trang của HoldMembers lên khóa cả bảng. Nâng khóa là cơ chế khiến một transaction
    /// lớn có thể chặn cả những unit không liên quan.
    /// </summary>
    private async Task<long> LockEscalationsAsync()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(Ct);
        using var command = new Microsoft.Data.SqlClient.SqlCommand("""
            SELECT coalesce(sum(index_lock_promotion_count), 0) * 1000000 + coalesce(sum(index_lock_promotion_attempt_count), 0)
            FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('quality.HoldMembers'), NULL, NULL);
            """, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private static string Serial(char kind, string line, int index) =>
        string.Create(CultureInfo.InvariantCulture,
            $"NV1{kind}{line}{(index / 90_000) + 5}{index / 30_000 % 3 + 101:D3}{"ABC"[index / 10_000 % 3]}{index % 10_000 + 1:D5}");

    private static IEnumerable<BulkStreamEvent> Dataset()
    {
        var packs = PacksPerLot * 2;
        var cells = packs * ModulesPerPack * CellsPerModule;
        for (var i = 0; i < cells; i++)
        {
            var cell = Serial('C', "L1", i);
            var lot = i < cells / 2 ? "ELY-A" : "ELY-B";
            yield return Fact("consumption:" + cell, "unit-consumption", new MaterialLotConsumed(Guid.NewGuid(), T0, T0,
                "NV1", lot, "Lot", "ELECTROLYTE", cell, 1m, "g", null, null, "RUN", "seed"));
            yield return Fact("membership:" + cell, "unit-membership", new UnitAssembledInto(Guid.NewGuid(), T0, T0, "NV1",
                cell, Serial('M', "M1", i / CellsPerModule), "S01", "RUN", "seed"));
        }
        for (var m = 0; m < packs * ModulesPerPack; m++)
        {
            var module = Serial('M', "M1", m);
            yield return Fact("membership:" + module, "unit-membership", new UnitAssembledInto(Guid.NewGuid(), T0, T0, "NV1",
                module, Serial('P', "P1", m / ModulesPerPack), "M1", "RUN", "seed"));
        }
    }

    private static BulkStreamEvent Fact(string stream, string type, IDomainEvent value) =>
        new(stream, type, DomainEventRecord.Create(value, "urn:lab", "NV1:" + stream, T0));
}
