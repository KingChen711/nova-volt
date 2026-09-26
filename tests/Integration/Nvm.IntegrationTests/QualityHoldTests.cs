using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.CommandStore;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Ports;
using Nvm.EventStore;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Material.Commands;
using Nvm.ProductionExecution.Commands;
using Nvm.Projections;
using Nvm.Quality.Commands;
using Nvm.Quality.Entities;
using Nvm.Quality.Hosting;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>M9: hold cascade theo span, idempotent, resume; thả hold cần hai chữ ký khác người giữ; chuỗi hash.</summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class QualityHoldTests(SqlCommandStoreFixture sql) : IClassFixture<SqlCommandStoreFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string C1 = "NV1CL16263A04001";
    private const string C2 = "NV1CL16263A04002";
    private const string C3 = "NV1CL16263A04003";
    private const string C4 = "NV1CL16263A04004";
    private const string M1 = "NV1MM16263A04001";
    private const string M2 = "NV1MM16263A04002";

    [Fact]
    public async Task SpanHold_HoldsExactlyTheAffectedUnits_ReleaseNeedsTwoSignaturesFromOthers_AndChainDetectsTampering()
    {
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(Ct);
        await using var data = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await ProjectionSchemaMigrator.UpgradeAsync(data, Ct);
        var clock = new FakeTimeProvider(T0);
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock, data);

        foreach (var cell in new[] { C1, C2, C3, C4 })
        { (await Serialize(host, cell, TraceabilityFixtureSeed.ProductCode)).Accepted.ShouldBeTrue(); }
        foreach (var module in new[] { M1, M2 })
        { (await Serialize(host, module, TraceabilityFixtureSeed.PackProductCode)).Accepted.ShouldBeTrue(); }
        (await host.DispatchAsync(new RecordRollCoatedCommand("NV1", "op", "coat", T0, "ROLL-Q",
            [new("A", 0m, 3000m, "SLR", "FOIL", "RCP", "COAT-01")]), Ct)).Accepted.ShouldBeTrue();
        foreach (var (cell, meter) in new[] { (C1, 1260m), (C2, 1400m), (C3, 2000m), (C4, 2100m) })
        {
            (await host.DispatchAsync(new ConsumeMaterialCommand("NV1", "op", "use-" + cell, T0, cell, "ROLL-Q", "Roll",
                "ANODE", 0.82m, "m", meter, meter + 0.82m, "RUN"), Ct)).Accepted.ShouldBeTrue();
        }
        foreach (var (cell, module) in new[] { (C1, M1), (C3, M1), (C2, M2), (C4, M2) })
        {
            (await host.DispatchAsync(new AssembleUnitCommand("NV1", "op", "asm-" + cell, cell, T0, module, "S01", "RUN"), Ct))
                .Accepted.ShouldBeTrue();
        }
        await new GenealogyProjection(data, new SqlGlobalEventFeed(sql.ConnectionString)).CatchUpAsync("NV1", cancellationToken: Ct);

        // Lỗi coating ở mét 1250–1430: chỉ C1, C2 và module chứa chúng bị giữ, không phải cả cuộn.
        var placed = await host.DispatchAsync(new PlaceHoldCommand("NV1", "qa.eng", "hold-span", T0, "Roll", "ROLL-Q",
            1250m, 1430m, "COATING_DEFECT", null), Ct);
        placed.Accepted.ShouldBeTrue();
        var holdId = placed.ReasonText!;
        var worker = Worker(host);
        (await worker.RunOnceAsync(Ct)).ShouldBe(1);
        (await Members(holdId)).ShouldBe([C1, C2, M1, M2]);
        (await worker.RunOnceAsync(Ct)).ShouldBe(0);   // chạy lại: không chunk nào, không event trùng
        (await ReadStore().ReadStreamAsync("NV1", "cascade:" + holdId, Ct))!.Events.Length.ShouldBe(3);

        (await Start(host, C1, "blocked")).ReasonCode.ShouldBe("QUALITY_HOLD");
        (await Start(host, C3, "free")).Accepted.ShouldBeTrue();

        var view = await host.GetRequiredService<SqlQualityQueries>().HoldAsync("NV1", holdId, Ct);
        view!.CascadeHeld.ShouldBe(4);
        (await Release(host, "qa.eng", holdId, "self-release", ["SIG-ANY"])).ReasonCode.ShouldBe(QualityReasonCodes.SeparationOfDuties);
        var qm = await Sign(host, "qa.manager", ApprovalPolicy.QualityManager, "QualityHold", holdId, view.ReleaseContentSha256);
        (await Release(host, "prod.manager", holdId, "one-signature", [qm])).ReasonCode.ShouldBe(QualityReasonCodes.MissingSignature);
        var byHolder = await Sign(host, "qa.eng", ApprovalPolicy.ProductionManager, "QualityHold", holdId, view.ReleaseContentSha256);
        (await Release(host, "prod.manager", holdId, "holder-signed", [qm, byHolder])).ReasonCode
            .ShouldBe(QualityReasonCodes.SeparationOfDuties);
        (await host.DispatchAsync(new SignCommand("NV1", "prod.manager", "stale", T0, "QualityHold", holdId, "Approved",
            ApprovalPolicy.ProductionManager, new string('a', 64), null), Ct)).ReasonCode.ShouldBe(QualityReasonCodes.SignatureStaleContent);
        var pm = await Sign(host, "prod.manager", ApprovalPolicy.ProductionManager, "QualityHold", holdId, view.ReleaseContentSha256);
        (await Release(host, "shift.lead", holdId, "released", [qm, pm])).Accepted.ShouldBeTrue();
        (await Start(host, C1, "after-release")).Accepted.ShouldBeTrue();

        // MRB: NCR cho C4 → Scrap cần QualityManager + ProductionManager, không ai là người mở NCR.
        var ncr = await RaiseNcrAsync(C4);
        (await host.DispatchAsync(new ApplyDispositionCommand("NV1", "qa.manager", "scrap-1", T0, ncr.NcrId, "Scrap",
            [await Sign(host, "qa.manager", ApprovalPolicy.QualityManager, "NonConformance", ncr.NcrId,
                DispositionContent(ncr.NcrId, C4, "Scrap"), "Scrap")]), Ct)).ReasonCode.ShouldBe(QualityReasonCodes.MissingSignature);
        var scrapQm = await Sign(host, "qa.manager", ApprovalPolicy.QualityManager, "NonConformance", ncr.NcrId,
            DispositionContent(ncr.NcrId, C4, "Scrap"), "Scrap", "scrap-qm-2");
        var scrapPm = await Sign(host, "prod.manager", ApprovalPolicy.ProductionManager, "NonConformance", ncr.NcrId,
            DispositionContent(ncr.NcrId, C4, "Scrap"), "Scrap");
        (await host.DispatchAsync(new ApplyDispositionCommand("NV1", "qa.manager", "scrap-2", T0, ncr.NcrId, "Scrap",
            [scrapQm, scrapPm]), Ct)).Accepted.ShouldBeTrue();
        (await sql.ScalarAsync("SELECT QualityState FROM quality.UnitQuality WHERE SerialNumber = 'NV1CL16263A04004';",
            _ => { }, Ct)).ShouldBe("Scrapped");

        var queries = host.GetRequiredService<SqlQualityQueries>();
        var chain = await queries.SignatureChainAsync("NV1", Ct);
        SignatureChain.FirstBroken(chain).ShouldBeNull();
        chain.Count.ShouldBeGreaterThanOrEqualTo(6);
        // Sửa một byte của nội dung đã ký (quyền sa, ngoài ứng dụng): chuỗi phải gãy đúng tại chữ ký đó.
        var victim = chain[2];
        var tampered = (victim.ContentSha256[0] == 'a' ? "b" : "a") + victim.ContentSha256[1..];
        await sql.ExecuteAsync("UPDATE quality.Signatures SET ContentSha256 = @c WHERE SignatureId = @id;", command =>
        {
            command.Parameters.AddWithValue("@c", tampered);
            command.Parameters.AddWithValue("@id", victim.SignatureId);
        }, Ct);
        SignatureChain.FirstBroken(await queries.SignatureChainAsync("NV1", Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task KilledCascade_ResumesFromCheckpoint_WithoutRedoingCommittedChunks()
    {
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(Ct);
        await using var data = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await ProjectionSchemaMigrator.UpgradeAsync(data, Ct);
        const int cells = 2_400;
        await SqlEventBulkLoader.LoadAsync(sql.ConnectionString, "NV1", Enumerable.Range(0, cells).Select(i =>
        {
            var serial = string.Create(CultureInfo.InvariantCulture, $"NV1CL16264B{i + 1:D5}");
            return new BulkStreamEvent("consumption:" + serial, "unit-consumption", DomainEventRecord.Create(
                new MaterialLotConsumed(Guid.NewGuid(), T0, T0, "NV1", "ELY-RESUME", "Lot", "ELECTROLYTE", serial, 1m, "g",
                    null, null, "RUN", "seed"), "urn:lot", "NV1:" + serial, T0));
        }), cancellationToken: Ct);
        await new GenealogyProjection(data, new SqlGlobalEventFeed(sql.ConnectionString)).CatchUpAsync("NV1", cancellationToken: Ct);
        var clock = new FakeTimeProvider(T0);
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock, data);
        var holdId = (await host.DispatchAsync(new PlaceHoldCommand("NV1", "qa.eng", "hold-lot", T0, "Lot", "ELY-RESUME",
            null, null, "CONTAMINATION", null), Ct)).ReasonText!;
        // "Tiến trình chết" sau chunk đầu: chỉ plan và một chunk được commit.
        (await host.DispatchAsync(new PlanHoldCascadeCommand("NV1", holdId, 0, T0), Ct)).StreamVersion.ShouldBe(cells);
        (await host.DispatchAsync(new ApplyCascadeChunkCommand("NV1", holdId, 0, T0), Ct)).Accepted.ShouldBeTrue();
        (await Members(holdId)).Length.ShouldBe(1_000);

        (await Worker(host).RunOnceAsync(Ct)).ShouldBe(2);   // tiếp tục chunk 1, 2 — không làm lại chunk 0
        (await Members(holdId)).Length.ShouldBe(cells);
        var stream = await ReadStore().ReadStreamAsync("NV1", "cascade:" + holdId, Ct);
        stream!.Events.Count(e => e.EventType.EndsWith("units-held-by-cascade.v1", StringComparison.Ordinal)).ShouldBe(3);
        stream.Events.Count(e => e.EventType.EndsWith("hold-cascade-completed.v1", StringComparison.Ordinal)).ShouldBe(1);
    }

    private static HoldCascadeWorker Worker(ServiceProvider host) =>
        new(host.GetRequiredService<IServiceScopeFactory>(), host.GetRequiredService<SqlCommandStoreOptions>(),
            new HoldCascadeOptions(TimeSpan.Zero), host.GetRequiredService<TimeProvider>(), NullLogger<HoldCascadeWorker>.Instance);

    private static Task<UnitCommandResult> Serialize(ServiceProvider host, string serial, string product) =>
        host.DispatchAsync(new SerializeUnitCommand("NV1", "op", "birth-" + serial, serial, T0, product, "WO-M9",
            TraceabilityFixtureSeed.RoutingVersion), Ct);

    private static Task<UnitCommandResult> Start(ServiceProvider host, string serial, string submission) =>
        host.DispatchAsync(new StartStepCommand("NV1", "op", submission + serial, serial, T0, "STACK", "run-" + submission,
            "NOVAVOLT/NV1/ASSEMBLY/L1/STACK-01"), Ct);

    private static Task<DomainCommandResult> Release(ServiceProvider host, string actor, string holdId, string submission,
        string[] signatures) =>
        host.DispatchAsync(new ReleaseHoldCommand("NV1", actor, submission, T0, holdId, [.. signatures]), Ct);

    private static async Task<string> Sign(ServiceProvider host, string actor, string role, string subjectType,
        string subjectId, string content, string? disposition = null, string? submission = null)
    {
        var result = await host.DispatchAsync(new SignCommand("NV1", actor, submission ?? $"sign-{actor}-{subjectId}-{role}",
            T0, subjectType, subjectId, "Approved", role, content, disposition), Ct);
        result.Accepted.ShouldBeTrue(result.ReasonCode);
        return result.ReasonText!;
    }

    private static string DispositionContent(string ncrId, string serial, string disposition) =>
        SignatureChain.Content(string.Join('|', "NCR-DISPOSITION", ncrId, serial, "TEST_DEFECT", disposition));

    private async Task<QualityIncident> RaiseNcrAsync(string serial)
    {
        QualityIncident? incident = null;
        var command = SqlCommandStoreFixture.NewCommand("NV1", "operator.nv1", Guid.NewGuid().ToString("N"), "raise ncr");
        await sql.SubmitAsync(command, async session =>
        {
            var events = new SqlEventStore(session, new SqlEventStoreOptions { ConnectionString = sql.ConnectionString },
                new EventUpcasterChain([]));
            var facet = new SqlUnitQualityFacet(session, events, TimeProvider.System);
            incident = await new SqlQualityIncidents(session, events, facet, TimeProvider.System)
                .QuarantineWithNonConformanceAsync("NV1", serial, "TEST_DEFECT", "Hàn tab lệch", "operator",
                    Guid.NewGuid(), "operator.nv1", T0, Ct);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);
        return incident!;
    }

    private async Task<string[]> Members(string holdId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(Ct);
        using var command = new SqlCommand(
            "SELECT SerialNumber FROM quality.HoldMembers WHERE HoldId = @hold ORDER BY SerialNumber;", connection);
        command.Parameters.AddWithValue("@hold", holdId);
        var serials = new List<string>();
        using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        { serials.Add(reader.GetString(0)); }
        return [.. serials];
    }

    private SqlEventStore ReadStore() =>
        new(new SqlCommandSession(), new SqlEventStoreOptions { ConnectionString = sql.ConnectionString }, new EventUpcasterChain([]));
}
