using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.CommandStore;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Events.Traceability;
using Nvm.EventStore;
using Nvm.Kernel.EventSourcing;
using Nvm.Projections;
using Nvm.Quality.Commands;
using Nvm.Quality.Hosting;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>Lab M9 (N9): hold một lot dùng cho 3.000 pack; đo thời gian cascade tới khi mọi unit hạ nguồn bị giữ.</summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class QualityCascadeScaleLabTests(SqlCommandStoreFixture sql, ITestOutputHelper output)
    : IClassFixture<SqlCommandStoreFixture>
{
    private const int Packs = 3_000;
    private const int ModulesPerPack = 8;
    private const int CellsPerModule = 12;
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HoldingOneLot_CascadesToThreeThousandPacks_InUnderSixtySeconds()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M9 cascade lab (ADR-017).");
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        var seed = Stopwatch.StartNew();
        await SqlEventBulkLoader.LoadAsync(sql.ConnectionString, "NV1", Dataset(), cancellationToken: Ct);
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(Ct);
        await using var data = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await ProjectionSchemaMigrator.UpgradeAsync(data, Ct);
        await new GenealogyProjection(data, new SqlGlobalEventFeed(sql.ConnectionString)).RebuildAsync("NV1", cancellationToken: Ct);
        seed.Stop();
        output.WriteLine($"SEED units={Packs * ModulesPerPack * (CellsPerModule + 1) + Packs} seconds={seed.Elapsed.TotalSeconds:F1}");

        var clock = new FakeTimeProvider(T0);
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock, data);
        var worker = new HoldCascadeWorker(host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<SqlCommandStoreOptions>(), new HoldCascadeOptions(TimeSpan.Zero), clock,
            NullLogger<HoldCascadeWorker>.Instance);
        var watch = Stopwatch.StartNew();
        var holdId = (await host.DispatchAsync(new PlaceHoldCommand("NV1", "qa.eng", "hold-n9", T0, "Lot", "ELY-N9", null,
            null, "CONTAMINATION", null), Ct)).ReasonText!;
        var chunks = await worker.RunOnceAsync(Ct);
        watch.Stop();
        var view = await host.GetRequiredService<SqlQualityQueries>().HoldAsync("NV1", holdId, Ct);
        var packsHeld = await sql.ScalarAsync("""
            SELECT CONVERT(varchar(10), count(*)) FROM quality.HoldMembers
            WHERE HoldId = @hold AND SUBSTRING(SerialNumber, 4, 1) = 'P';
            """, command => command.Parameters.AddWithValue("@hold", holdId), Ct);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CASCADE units={view!.CascadeHeld} packs={packsHeld} chunks={chunks} seconds={watch.Elapsed.TotalSeconds:F2} " +
            $"units_per_second={view.CascadeHeld / watch.Elapsed.TotalSeconds:F0}"));
        packsHeld.ShouldBe(Packs.ToString(CultureInfo.InvariantCulture));
        view.CascadeHeld.ShouldBe(Packs * ModulesPerPack * (CellsPerModule + 1) + Packs);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "N9: 3.000 pack in under 60 s");

        var second = Stopwatch.StartNew();
        (await worker.RunOnceAsync(Ct)).ShouldBe(0);
        output.WriteLine($"CASCADE_REPEAT chunks=0 seconds={second.Elapsed.TotalSeconds:F2}");
    }

    private static string Serial(char kind, string line, int index) =>
        string.Create(CultureInfo.InvariantCulture,
            $"NV1{kind}{line}{(index / 90_000) + 5}{index / 30_000 % 3 + 101:D3}{"ABC"[index / 10_000 % 3]}{index % 10_000 + 1:D5}");

    private static IEnumerable<BulkStreamEvent> Dataset()
    {
        var cells = Packs * ModulesPerPack * CellsPerModule;
        for (var i = 0; i < cells; i++)
        {
            var cell = Serial('C', "L1", i);
            yield return Fact("consumption:" + cell, "unit-consumption", new MaterialLotConsumed(Guid.NewGuid(), T0, T0,
                "NV1", "ELY-N9", "Lot", "ELECTROLYTE", cell, 1m, "g", null, null, "RUN", "seed"));
            yield return Fact("membership:" + cell, "unit-membership", new UnitAssembledInto(Guid.NewGuid(), T0, T0, "NV1",
                cell, Serial('M', "M1", i / CellsPerModule), "S01", "RUN", "seed"));
        }
        for (var m = 0; m < Packs * ModulesPerPack; m++)
        {
            var module = Serial('M', "M1", m);
            yield return Fact("membership:" + module, "unit-membership", new UnitAssembledInto(Guid.NewGuid(), T0, T0, "NV1",
                module, Serial('P', "P1", m / ModulesPerPack), "M1", "RUN", "seed"));
        }
    }

    private static BulkStreamEvent Fact(string stream, string type, IDomainEvent value) =>
        new(stream, type, DomainEventRecord.Create(value, "urn:lab", "NV1:" + stream, T0));
}
