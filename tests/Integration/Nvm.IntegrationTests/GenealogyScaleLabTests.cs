using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Npgsql;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Contracts.Events.Traceability;
using Nvm.EventStore;
using Nvm.Kernel.EventSourcing;
using Nvm.Projections;
using Nvm.PublicObjectModel;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// Lab M6 (N6/N7/N11): 100.000 cell → 8.000 module → 1.000 pack, mỗi cell 4 nguồn vật liệu.
/// Đo rebuild, forward/backward trace bằng closure và bằng recursive CTE. Số đo ghi vào ADR-006.
/// </summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class GenealogyScaleLabTests(SqlCommandStoreFixture sql, ITestOutputHelper output)
    : IClassFixture<SqlCommandStoreFixture>
{
    private const int Cells = 100_000;
    private const int CellsPerModule = 12;
    private const int ModulesPerPack = 8;
    private const int Modules = 8_000;
    private const int Packs = 1_000;
    private const int Samples = 200;
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HundredThousandCells_TraceP95AndRebuild_MeetN6N7N11()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M6 genealogy scale lab (ADR-006).");
        await EventSchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        var seed = Stopwatch.StartNew();
        var loaded = await SqlEventBulkLoader.LoadAsync(sql.ConnectionString, "NV1", Dataset(), cancellationToken: Ct);
        seed.Stop();
        output.WriteLine($"SEED events={loaded} seconds={seed.Elapsed.TotalSeconds:F1}");

        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(Ct);
        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await ProjectionSchemaMigrator.UpgradeAsync(dataSource, Ct);
        var projection = new GenealogyProjection(dataSource, new SqlGlobalEventFeed(sql.ConnectionString));
        var rebuild = await projection.RebuildAsync("NV1", cancellationToken: Ct);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"REBUILD facts={rebuild.FactsApplied} seconds={rebuild.Elapsed.TotalSeconds:F1}"));
        await using (var analyze = dataSource.CreateCommand("ANALYZE trace.genealogy_link; ANALYZE rm.genealogy_closure;"))
        { await analyze.ExecuteNonQueryAsync(Ct); }
        await using (var size = dataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM trace.genealogy_link), (SELECT count(*) FROM rm.genealogy_closure),
                pg_size_pretty(pg_total_relation_size('rm.genealogy_closure')),
                pg_size_pretty(pg_total_relation_size('trace.genealogy_link'));
            """))
        await using (var reader = await size.ExecuteReaderAsync(Ct))
        {
            await reader.ReadAsync(Ct);
            output.WriteLine($"SIZE links={reader.GetInt64(0)} closure_rows={reader.GetInt64(1)} " +
                $"closure_size={reader.GetString(2)} links_size={reader.GetString(3)}");
        }

        var queries = new TraceQueries(dataSource);
        var random = new Random(7);
        var lots = Enumerable.Range(0, Samples).Select(_ => random.Next(4) switch
        {
            0 => ((short)1, "ELY-" + random.Next(50).ToString("D3", CultureInfo.InvariantCulture)),
            1 => ((short)1, "SEP-" + random.Next(20).ToString("D3", CultureInfo.InvariantCulture)),
            2 => ((short)5, "CAT-" + random.Next(100).ToString("D3", CultureInfo.InvariantCulture)),
            _ => ((short)5, "ANO-" + random.Next(100).ToString("D3", CultureInfo.InvariantCulture)),
        }).ToArray();
        var packs = Enumerable.Range(0, Samples).Select(_ => Pack(random.Next(Packs))).ToArray();

        foreach (var (type, id) in lots.Take(20))
        {
            (await queries.ForwardAsync("NV1", type, id, 4, Ct)).Select(n => n.Id)
                .ShouldBe((await queries.ForwardByRecursionAsync("NV1", type, id, 4, Ct)).Select(n => n.Id));
        }
        foreach (var pack in packs.Take(20))
        {
            (await queries.BackwardAsync("NV1", 4, pack, 1, Ct)).Select(n => n.Id)
                .ShouldBe((await queries.BackwardByRecursionAsync("NV1", 4, pack, 1, Ct)).Select(n => n.Id));
        }

        var forwardClosure = await MeasureAsync(lots, (root, ct) => queries.ForwardAsync("NV1", root.Item1, root.Item2, 4, ct));
        var forwardCte = await MeasureAsync(lots, (root, ct) => queries.ForwardByRecursionAsync("NV1", root.Item1, root.Item2, 4, ct));
        var backwardClosure = await MeasureAsync(packs, (pack, ct) => queries.BackwardAsync("NV1", 4, pack, null, ct));
        var backwardCte = await MeasureAsync(packs, (pack, ct) => queries.BackwardByRecursionAsync("NV1", 4, pack, null, ct));
        var span = await MeasureAsync(lots.Where(l => l.Item1 == 5).ToArray(),
            (root, ct) => queries.RollSpanAsync("NV1", root.Item2, 250m, 430m, ct));
        Report("forward_closure", forwardClosure);
        Report("forward_cte", forwardCte);
        Report("backward_closure", backwardClosure);
        Report("backward_cte", backwardCte);
        Report("roll_span", span);

        forwardClosure.P95.ShouldBeLessThan(200, "N6 forward trace p95");
        backwardClosure.P95.ShouldBeLessThan(150, "N7 backward trace p95");
        rebuild.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(10), "N11 rebuild");
    }

    private void Report(string name, (double P50, double P95, double Max, double Rows) result) =>
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TRACE {name} samples={Samples} p50_ms={result.P50:F2} p95_ms={result.P95:F2} max_ms={result.Max:F2} avg_rows={result.Rows:F1}"));

    private static async Task<(double P50, double P95, double Max, double Rows)> MeasureAsync<T>(IReadOnlyList<T> roots,
        Func<T, CancellationToken, Task<IReadOnlyList<TraceNode>>> query)
    {
        var durations = new List<double>();
        var rows = 0L;
        foreach (var root in roots)
        {
            var started = Stopwatch.GetTimestamp();
            rows += (await query(root, Ct)).Count;
            durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        durations.Sort();
        double At(double p) => durations[Math.Max(0, (int)Math.Ceiling(durations.Count * p) - 1)];
        return (At(0.5), At(0.95), durations[^1], rows / (double)roots.Count);
    }

    private static string Cell(int index) => Serial('C', "L1", index);
    private static string Module(int index) => Serial('M', "M1", index);
    private static string Pack(int index) => Serial('P', "P1", index);

    /// <summary>Serial hợp lệ theo ADR-007: 10 ngày × 3 ca × tối đa 3.334 unit mỗi ca.</summary>
    private static string Serial(char kind, string line, int index)
    {
        var day = 244 + index / 10_002;
        var shift = "ABC"[index % 10_002 / 3_334];
        var sequence = index % 3_334 + 1;
        return string.Create(CultureInfo.InvariantCulture, $"NV1{kind}{line}6{day:D3}{shift}{sequence:D5}");
    }

    private static IEnumerable<BulkStreamEvent> Dataset()
    {
        foreach (var prefix in new[] { "CAT", "ANO" })
        {
            for (var roll = 0; roll < 100; roll++)
            {
                var id = prefix + "-" + roll.ToString("D3", CultureInfo.InvariantCulture);
                var segments = Enumerable.Range(0, 10).Select(i => new RollSegment("A", i * 100m, (i + 1) * 100m,
                    $"SLR-{prefix}-{roll}-{i}", "FOIL-" + prefix, "RCP-COAT-v7", "COAT-01")).ToImmutableArray();
                yield return Fact("roll:" + id, "electrode-roll",
                    new RollCoated(Guid.NewGuid(), Start, Start, "NV1", id, segments, "seed"), "urn:electrode-roll:" + id);
            }
        }
        for (var cell = 0; cell < Cells; cell++)
        {
            var serial = Cell(cell);
            var meter = cell % 1000;
            yield return Consume(serial, "CAT-" + (cell / 1000).ToString("D3", CultureInfo.InvariantCulture), "Roll", meter);
            yield return Consume(serial, "ANO-" + (cell / 1000).ToString("D3", CultureInfo.InvariantCulture), "Roll", meter);
            yield return Consume(serial, "ELY-" + (cell / 2000).ToString("D3", CultureInfo.InvariantCulture), "Lot", null);
            yield return Consume(serial, "SEP-" + (cell / 5000).ToString("D3", CultureInfo.InvariantCulture), "Lot", null);
        }
        for (var cell = 0; cell < Modules * CellsPerModule; cell++)
        { yield return Assemble(Cell(cell), Module(cell / CellsPerModule), "S" + (cell % CellsPerModule).ToString("D2", CultureInfo.InvariantCulture)); }
        for (var module = 0; module < Modules; module++)
        { yield return Assemble(Module(module), Pack(module / ModulesPerPack), "M" + (module % ModulesPerPack).ToString(CultureInfo.InvariantCulture)); }
    }

    private static BulkStreamEvent Consume(string serial, string lot, string kind, int? meter) =>
        Fact("consumption:" + serial, "unit-consumption", new MaterialLotConsumed(Guid.NewGuid(), Start, Start, "NV1", lot,
            kind, kind == "Roll" ? lot[..3] + "-ROLL" : lot[..3], serial, kind == "Roll" ? 0.82m : 1m,
            kind == "Roll" ? "m" : "g", meter, meter + 0.82m, "RUN-SEED", "seed"), "urn:material-lot:" + lot);

    private static BulkStreamEvent Assemble(string child, string parent, string position) =>
        Fact("membership:" + child, "unit-membership",
            new UnitAssembledInto(Guid.NewGuid(), Start, Start, "NV1", child, parent, position, "RUN-SEED", "seed"),
            "urn:trace-unit:" + child);

    private static BulkStreamEvent Fact(string stream, string type, IDomainEvent value, string subject) =>
        new(stream, type, DomainEventRecord.Create(value, subject, "NV1:" + stream, Start));
}
