using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.CommandStore;
using Nvm.Contracts.Events.Grading;
using Nvm.EventStore;
using Nvm.Grading.Commands;
using Nvm.Grading.Handlers;
using Nvm.Grading.Hosting;
using Nvm.Kernel.Commands;
using Nvm.Material.Commands;
using Nvm.ProductionExecution.Hosting;
using Nvm.Projections;
using Nvm.PublicObjectModel;
using Nvm.Quality.Hosting;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>M8: rule set có version, grade không ghi đè, đánh giá lại, kho bin từ event thật.</summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class GradingTests(SqlCommandStoreFixture sql) : IClassFixture<SqlCommandStoreFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Product = TraceabilityFixtureSeed.ProductCode;
    private const string Old = "NV1CL16253A03001";
    private const string New = "NV1CL16253A03002";
    private const string Module = "NV1MM16253A03001";

    [Fact]
    public async Task NewRuleVersionGradesNewCells_OldGradesStay_AndReevaluationAppendsANewEvaluation()
    {
        await EventSchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await ProductionExecutionSchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await QualitySchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await GradingSchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await TraceabilityFixtureSeed.PrepareAsync(sql.ConnectionString, Ct);
        var clock = new FakeTimeProvider(T0);
        await using var host = Build(clock);
        foreach (var cell in new[] { Old, New })
        {
            (await Dispatch<UnitCommandResult>(host, new SerializeUnitCommand("NV1", "op", "birth-" + cell, cell, T0,
                Product, "WO-M8", TraceabilityFixtureSeed.RoutingVersion))).Accepted.ShouldBeTrue();
        }
        (await Dispatch<UnitCommandResult>(host, new SerializeUnitCommand("NV1", "op", "birth-module", Module, T0,
            TraceabilityFixtureSeed.PackProductCode, "WO-M8", TraceabilityFixtureSeed.RoutingVersion))).Accepted.ShouldBeTrue();

        (await Define("v1", 1, T0, Bins(120.0m))).Accepted.ShouldBeTrue();
        (await Approve("self-approve", 1, "qa.author")).ReasonCode.ShouldBe(GradingReasonCodes.SeparationOfDuties);
        (await Approve("approve-1", 1, "qa.lead")).Accepted.ShouldBeTrue();
        (await Dispatch<DomainCommandResult>(host, new DefineGradingRuleSetCommand("NV1", "qa.author", "clash", T0,
            "RS-OTHER", 1, Product, T0, Bins(118m), []))).Accepted.ShouldBeTrue();
        (await Dispatch<DomainCommandResult>(host, new ApproveGradingRuleSetCommand("NV1", "qa.lead", "clash-approve", T0,
            "RS-OTHER", 1))).ReasonCode.ShouldBe(GradingReasonCodes.EffectiveDateTaken);

        (await Grade("grade-old", Old, T0.AddDays(1))).ReasonText.ShouldBe("Bin A1.");
        (await Define("v2", 2, T0.AddDays(2), Bins(119.8m))).Accepted.ShouldBeTrue();
        (await Approve("approve-2", 2, "qa.lead")).Accepted.ShouldBeTrue();
        (await Grade("grade-new", New, T0.AddDays(3))).ReasonText.ShouldBe("Bin A0.");

        var oldStream = await ReadStore().ReadStreamAsync("NV1", GradingProcessor.StreamId(Old), Ct);
        var first = JsonSerializer.Deserialize<UnitGraded>(oldStream!.Events.Single().PayloadJson, Json)!;
        first.RuleSetVersion.ShouldBe(1);
        first.BinCode.ShouldBe("A1");
        clock.Advance(TimeSpan.FromDays(4));
        (await Dispatch<DomainCommandResult>(host, new ReevaluateUnitCommand("NV1", "qa.lead", "re-old", clock.GetUtcNow(), Old)))
            .Accepted.ShouldBeTrue();
        (await Dispatch<DomainCommandResult>(host, new ReevaluateUnitCommand("NV1", "qa.lead", "re-old-again", clock.GetUtcNow(), Old)))
            .ReasonCode.ShouldBe(GradingReasonCodes.SameRuleSet);
        oldStream = await ReadStore().ReadStreamAsync("NV1", GradingProcessor.StreamId(Old), Ct);
        oldStream!.Events.Length.ShouldBe(2);
        var second = JsonSerializer.Deserialize<UnitGraded>(oldStream.Events[1].PayloadJson, Json)!;
        second.RuleSetVersion.ShouldBe(2);
        second.MeasurementId.ShouldBe(first.MeasurementId);
        second.SupersedesEvaluationId.ShouldBe(first.EventId);
        second.BinCode.ShouldBe("A0");
        JsonSerializer.Deserialize<UnitGraded>(oldStream.Events[0].PayloadJson, Json)!.BinCode.ShouldBe("A1");

        (await Dispatch<DomainCommandResult>(host, new ConsumeMaterialCommand("NV1", "op", "roll-new", T0, New, "CAT-7",
            "Roll", "CAT-ROLL", 0.82m, "m", 10m, 10.82m, "RUN"))).Accepted.ShouldBeTrue();

        // Kho bin dựng từ feed SQL thật; cell đã lắp vào module rời kho.
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(Ct);
        await using var data = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await ProjectionSchemaMigrator.UpgradeAsync(data, Ct);
        var feed = new SqlGlobalEventFeed(sql.ConnectionString);
        var runner = new OrderedProjectionRunner(data, feed, [new BinInventoryProjection()]);
        await new GenealogyProjection(data, feed).CatchUpAsync("NV1", cancellationToken: Ct);
        await runner.CatchUpAsync("NV1", cancellationToken: Ct);
        var inventory = new BinInventoryQueries(data);
        var available = await inventory.AvailableAsync("NV1", Product, Ct);
        available.Select(c => (c.SerialNumber, c.BinCode, c.LotId)).ShouldBe([(Old, "A0", "UNKNOWN"), (New, "A0", "CAT-7")]);
        (await inventory.AvailableAsync("DE1", Product, Ct)).ShouldBeEmpty();

        (await Dispatch<UnitCommandResult>(host, new AssembleUnitCommand("NV1", "op", "assemble-old", Old, T0, Module, "S01",
            "RUN"))).Accepted.ShouldBeTrue();
        await runner.CatchUpAsync("NV1", cancellationToken: Ct);
        await runner.CatchUpAsync("NV1", cancellationToken: Ct);
        (await inventory.CountsAsync("NV1", Product, Ct)).ShouldBe([new BinCount("A0", 1)]);
        await runner.RebuildAsync("NV1", "bin-inventory-v1", Ct);
        (await inventory.CountsAsync("NV1", Product, Ct)).ShouldBe([new BinCount("A0", 1)]);

        async Task<DomainCommandResult> Define(string submission, int version, DateTimeOffset effective,
            ImmutableArray<GradingBin> bins) =>
            await Dispatch<DomainCommandResult>(host, new DefineGradingRuleSetCommand("NV1", "qa.author", submission, T0,
                "RS-A", version, Product, effective, bins, [new GradingReject("DCIR_HIGH", "DcirMilliOhm", 1.2m, false)]));

        async Task<DomainCommandResult> Approve(string submission, int version, string approver) =>
            await Dispatch<DomainCommandResult>(host, new ApproveGradingRuleSetCommand("NV1", approver, submission, T0,
                "RS-A", version));

        async Task<DomainCommandResult> Grade(string submission, string serial, DateTimeOffset at) =>
            await Dispatch<DomainCommandResult>(host, new GradeUnitCommand("NV1", "op", submission, at, serial, 119.9m,
                3650m, 0.9m, 3m));
    }

    /// <summary>Hai bin quanh ngưỡng: đổi ngưỡng giữa v1 và v2 làm cùng số đo 119,9 Ah rơi vào bin khác.</summary>
    private static ImmutableArray<GradingBin> Bins(decimal split) =>
        [new("A0", split, 125m, 3000m, 4500m, 0m, 2m, 1), new("A1", 110m, split, 3000m, 4500m, 0m, 2m, 2)];

    private SqlEventStore ReadStore() =>
        new(new SqlCommandSession(), new SqlEventStoreOptions { ConnectionString = sql.ConnectionString }, new EventUpcasterChain([]));

    private ServiceProvider Build(TimeProvider clock) => TestCommandHost.Build(sql.ConnectionString, clock);

    private static async Task<TResult> Dispatch<TResult>(ServiceProvider provider, ICommand<TResult> command)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>().DispatchAsync<TResult>(command, Ct);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Nvm.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
