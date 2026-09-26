using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Nvm.CommandStore;
using Nvm.EventStore;
using Nvm.Kernel.EventSourcing;

namespace Nvm.IntegrationTests;

/// <summary>
/// Lab phá hoại M5 (scope §6.2): 96 cell của một pack cùng ghi đồng thời. Biến thể sai gom pack + cell vào
/// một aggregate; biến thể đúng để mỗi cell một stream, quan hệ pack–cell là GenealogyLink riêng.
/// Số đo được in ra và ghi vào ADR-044.
/// </summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class AggregateBoundaryLabTests(SqlCommandStoreFixture fixture, ITestOutputHelper output)
    : IClassFixture<SqlCommandStoreFixture>
{
    private const int Cells = 96;
    private const int MeasurementsPerCell = 5;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NinetySixConcurrentCells_OnePackAggregate_VersusOneStreamPerCell()
    {
        // Biến thể optimistic tốn khoảng 150 s; lab chạy theo yêu cầu, không nằm trong suite N14.
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M5 aggregate boundary lab (ADR-044).");
        await EventSchemaMigrator.UpgradeAsync(fixture.ConnectionString, Ct);
        var pack = "NV1PP16267A" + Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);

        // Store thật khoá stream ngay khi đọc trong transaction (UPDLOCK): tranh chấp biến thành chờ lock.
        var locked = await RunAsync("single-aggregate-locked-read",
            cell => ("pack-aggregate-a:" + pack, "pack-aggregate"), linkPerCell: false, optimistic: false);
        // Đọc ngoài transaction rồi append với expectedVersion: tranh chấp biến thành ConcurrencyException.
        var optimistic = await RunAsync("single-aggregate-optimistic",
            cell => ("pack-aggregate-b:" + pack, "pack-aggregate"), linkPerCell: false, optimistic: true);
        var split = await RunAsync("stream-per-unit",
            cell => ("cell:" + pack + ":" + cell, "production-unit"), linkPerCell: true, optimistic: true);

        output.WriteLine(locked.Describe());
        output.WriteLine(optimistic.Describe());
        output.WriteLine(split.Describe());
        optimistic.Conflicts.ShouldBeGreaterThan(0, "one pack stream must reject concurrent cell writers");
        split.Conflicts.ShouldBe(0);
        split.P99.ShouldBeLessThan(locked.P99);
        split.P99.ShouldBeLessThan(optimistic.P99);
        foreach (var result in new[] { locked, optimistic, split })
        { result.Committed.ShouldBe(Cells * MeasurementsPerCell); }
    }

    private async Task<LabResult> RunAsync(string variant, Func<int, (string Stream, string Type)> target,
        bool linkPerCell, bool optimistic)
    {
        var latencies = new ConcurrentBag<double>();
        var conflicts = 0;
        var committed = 0;
        var loaded = 0L;
        if (linkPerCell)
        {
            // Quan hệ pack–cell là một GenealogyLink riêng cho từng cell: không stream nào bị 96 writer chia sẻ.
            await Parallel.ForEachAsync(Enumerable.Range(0, Cells), Ct, async (cell, ct) =>
                await AppendOnceAsync($"link:{variant}:{cell}", "genealogy-link", 0, "linked", ct));
        }

        var wall = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Cells).Select(cell => Task.Run(async () =>
        {
            var (stream, type) = target(cell);
            for (var measurement = 0; measurement < MeasurementsPerCell; measurement++)
            {
                var started = Stopwatch.GetTimestamp();
                var id = Guid.NewGuid();
                while (true)
                {
                    var session = new SqlCommandSession();
                    var command = SqlCommandStoreFixture.NewCommand("NV1", "lab", id.ToString("N"), variant);
                    try
                    {
                        await fixture.SubmitAsync(command, async active =>
                        {
                            var store = Store(active);
                            // Load aggregate như handler: replay toàn bộ stream trước khi quyết định.
                            var current = await (optimistic ? Store(new SqlCommandSession()) : store)
                                .ReadStreamAsync("NV1", stream, Ct);
                            Interlocked.Add(ref loaded, current?.Events.Length ?? 0);
                            await store.AppendAsync("NV1", stream, type, current?.Version ?? 0,
                                ImmutableArray.Create(Fact(id, cell, measurement)), Ct);
                            return new CollectionOutcome(true, "OK");
                        }, cancellationToken: Ct);
                        break;
                    }
                    catch (EventConcurrencyException)
                    {
                        Interlocked.Increment(ref conflicts);
                        await Task.Delay(Random.Shared.Next(1, 5), Ct);
                    }
                    finally { await session.DisposeAsync(); }
                }
                Interlocked.Increment(ref committed);
                latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }, Ct)));
        wall.Stop();

        var sorted = latencies.Order().ToArray();
        return new LabResult(variant, committed, conflicts, Percentile(sorted, 0.50), Percentile(sorted, 0.95),
            Percentile(sorted, 0.99), sorted[^1], wall.Elapsed.TotalSeconds, loaded);
    }

    private async Task AppendOnceAsync(string stream, string type, long expected, string value, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var command = SqlCommandStoreFixture.NewCommand("NV1", "lab", id.ToString("N"), stream);
        await fixture.SubmitAsync(command, async session =>
        {
            await Store(session).AppendAsync("NV1", stream, type, expected,
                ImmutableArray.Create(Fact(id, 0, 0, value)), ct);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: ct);
    }

    private static double Percentile(double[] sorted, double p) =>
        sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * p) - 1)];

    private SqlEventStore Store(SqlCommandSession session) =>
        new(session, new SqlEventStoreOptions { ConnectionString = fixture.ConnectionString }, new EventUpcasterChain([]));

    private static NewStreamEvent Fact(Guid id, int cell, int measurement, string kind = "measured") =>
        new(id, "com.novavolt.lab.cell-" + kind + ".v1", 1,
            JsonSerializer.Serialize(new { siteId = "NV1", cell, measurement, voltage = 3.7 }), "{}",
            TimeProvider.System.GetUtcNow(), TimeProvider.System.GetUtcNow());

    private sealed record LabResult(string Variant, int Committed, int Conflicts, double P50, double P95,
        double P99, double Max, double WallSeconds, long EventsReplayed)
    {
        public string Describe() => string.Create(CultureInfo.InvariantCulture,
            $"AGGREGATE_LAB variant={Variant} cells={Cells} commands={Committed} conflicts={Conflicts} " +
            $"p50_ms={P50:F1} p95_ms={P95:F1} p99_ms={P99:F1} max_ms={Max:F1} wall_s={WallSeconds:F2} " +
            $"events_replayed={EventsReplayed}");
    }
}
