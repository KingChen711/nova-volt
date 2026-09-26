using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nvm.Contracts.Events.Recipe;
using Nvm.Kernel.Commands;
using Nvm.Material.Commands;
using Nvm.Material.Entities;
using Nvm.Material.Handlers;
using Nvm.Quality.Commands;
using Nvm.Recipe.Commands;
using Nvm.Recipe.Hosting;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;

namespace Nvm.IntegrationTests;

/// <summary>M10: recipe có hiệu lực theo thời gian, một version active chặn ở DB; lot vật liệu bị chặn có lý do cụ thể.</summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class RecipeMaterialTests(SqlCommandStoreFixture sql) : IClassFixture<SqlCommandStoreFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 12, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Coater = "NOVAVOLT/NV1/ELECTRODE/C1/COAT-01";

    [Fact]
    public async Task RecipeAtAMoment_IsTheVersionActiveThen_AndTheDatabaseRefusesTwoActiveVersions()
    {
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        var clock = new FakeTimeProvider(T0);
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock);
        var v1 = await Define(host, 1, 118m);
        (await Approve(host, 1, T0, [await Sign(host, "qa.manager", "QaManager", 1, v1)]))
            .ReasonCode.ShouldBe("MISSING_SIGNATURE");
        (await Approve(host, 1, T0, [await Sign(host, "qa.eng", "QaManager", 1, v1, "author"),
            await Sign(host, "prod.manager", "ProductionManager", 1, v1)])).ReasonCode.ShouldBe("SEPARATION_OF_DUTIES");
        (await Approve(host, 1, T0, [await Sign(host, "qa.manager", "QaManager", 1, v1, "qm2"),
            await Sign(host, "prod.manager", "ProductionManager", 1, v1, "pm2")])).Accepted.ShouldBeTrue();
        var v2 = await Define(host, 2, 121m);
        (await Approve(host, 2, T0.AddDays(2), [await Sign(host, "qa.manager", "QaManager", 2, v2),
            await Sign(host, "prod.manager", "ProductionManager", 2, v2)])).Accepted.ShouldBeTrue();

        (await Apply(host, T0.AddDays(1), "run-1")).ReasonText.ShouldBe("RCP-COAT v1");
        (await Apply(host, T0.AddDays(3), "run-2")).ReasonText.ShouldBe("RCP-COAT v2");
        var queries = host.GetRequiredService<RecipeQueries>();
        var at1420 = await queries.AppliedAtAsync("NV1", Coater, T0.AddDays(1).AddHours(14).AddMinutes(20), Ct);
        at1420!.Version.ShouldBe(1);
        at1420.ContentSha256.ShouldBe(v1);
        (await queries.AppliedAtAsync("NV1", Coater, T0.AddDays(3).AddHours(1), Ct))!.Version.ShouldBe(2);
        (await queries.AppliedAtAsync("NV1", Coater, T0.AddHours(-1), Ct)).ShouldBeNull();
        (await queries.AppliedAtAsync("DE1", Coater, T0.AddDays(3), Ct)).ShouldBeNull();

        // Kể cả khi code bị lách: DB không cho version cũ quay lại Active song song với version mới.
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(Ct);
        using var bypass = new SqlCommand("""
            UPDATE recipe.RecipeVersions SET Status = 'Active', EffectiveTo = NULL
            WHERE SiteId = 'NV1' AND RecipeId = 'RCP-COAT' AND Version = 1;
            """, connection);
        (await Should.ThrowAsync<SqlException>(() => bypass.ExecuteNonQueryAsync(Ct))).Number.ShouldBeOneOf(2601, 2627);
    }

    [Fact]
    public async Task ConsumingALot_IsBlockedWithTheExactReason_UntilAnOverrideIsSigned()
    {
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        var clock = new FakeTimeProvider(T0);
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock);
        const string cell = "NV1CL16071A07001";
        (await host.DispatchAsync(new SerializeUnitCommand("NV1", "op", "birth-m10", cell, T0, TraceabilityFixtureSeed.ProductCode,
            "WO-M10", TraceabilityFixtureSeed.RoutingVersion), Ct)).Accepted.ShouldBeTrue();
        await Receive(host, "ELY-OLD", T0.AddDays(-2), "g");
        await Receive(host, "ELY-NEW", T0, "g");
        (await Consume(host, "ELY-NEW", "g", "not-released")).ReasonCode.ShouldBe(MaterialRules.NotReleased);
        await Release(host, "ELY-NEW");
        await Open(host, "ELY-NEW");
        clock.Advance(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(32));
        var exposed = await Consume(host, "ELY-NEW", "g", "exposed");
        exposed.ReasonCode.ShouldBe(MaterialRules.ExposureExceeded);
        exposed.ReasonText.ShouldBe("Lot đã mở 4h32m / giới hạn 4h00m.");
        (await Consume(host, "ELY-NEW", "kg", "wrong-unit")).ReasonCode.ShouldBe(MaterialRules.UomMismatch);

        var content = MaterialLotProcessor.OverrideContent("ELY-NEW", MaterialRules.ExposureExceeded, "Mẻ khẩn, đo ẩm đạt",
            T0.AddHours(8));
        var signature = (await host.DispatchAsync(new SignCommand("NV1", "qa.manager", "sign-override", T0, "MaterialOverride",
            "ELY-NEW", "Approved", "QaManager", content, null), Ct)).ReasonText!;
        (await host.DispatchAsync(new GrantMaterialOverrideCommand("NV1", "line.lead", "override", T0, "ELY-NEW",
            MaterialRules.ExposureExceeded, "Mẻ khẩn, đo ẩm đạt", T0.AddHours(8), [signature]), Ct)).Accepted.ShouldBeTrue();
        // Override chỉ mở luật tiếp xúc; FIFO vẫn chặn vì lot cũ hơn đã release và còn hàng.
        await Release(host, "ELY-OLD");
        var fifo = await Consume(host, "ELY-NEW", "g", "fifo");
        fifo.ReasonCode.ShouldBe(MaterialRules.Fifo);
        fifo.ReasonText!.ShouldContain("ELY-OLD");
        (await Consume(host, "ELY-OLD", "g", "old-first")).Accepted.ShouldBeTrue();

        (await host.DispatchAsync(new PlaceHoldCommand("NV1", "qa.eng", "hold-old", T0, "Lot", "ELY-OLD", null, null,
            "MOISTURE", null), Ct)).Accepted.ShouldBeTrue();
        (await Consume(host, "ELY-OLD", "g", "held")).ReasonCode.ShouldBe(MaterialReasonCodes.LotOnHold);
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(Ct);
        using var remaining = new SqlCommand("SELECT Remaining FROM material.Lots WHERE SiteId = 'NV1' AND LotId = 'ELY-OLD';",
            connection);
        ((decimal)(await remaining.ExecuteScalarAsync(Ct))!).ShouldBe(995m);
    }

    private static async Task<string> Define(ServiceProvider host, int version, decimal thickness)
    {
        var result = await host.DispatchAsync(new DefineRecipeVersionCommand("NV1", "qa.eng", "define-" + version, T0,
            "RCP-COAT", version, "NV-P120-NMC", "COAT", "COATER",
            [new RecipeParameter("WetThickness", thickness, thickness - 2m, thickness + 2m, "um"),
             new RecipeParameter("OvenTemperature", 130m, 125m, 135m, "degC")]), Ct);
        result.Accepted.ShouldBeTrue(result.ReasonCode);
        return result.ReasonText!;
    }

    private static Task<DomainCommandResult> Approve(ServiceProvider host, int version, DateTimeOffset effective, string[] signatures) =>
        host.DispatchAsync(new ApproveRecipeVersionCommand("NV1", "qa.manager", $"approve-{version}-{signatures.Length}-{signatures[0]}",
            T0, "RCP-COAT", version, effective, [.. signatures]), Ct);

    private static async Task<string> Sign(ServiceProvider host, string actor, string role, int version, string content,
        string tag = "")
    {
        var result = await host.DispatchAsync(new SignCommand("NV1", actor, $"sign-{actor}-{role}-{version}-{tag}", T0,
            "RecipeVersion", $"RCP-COAT:v{version}", "Approved", role, content, null), Ct);
        result.Accepted.ShouldBeTrue(result.ReasonCode);
        return result.ReasonText!;
    }

    private static Task<DomainCommandResult> Apply(ServiceProvider host, DateTimeOffset at, string run) =>
        host.DispatchAsync(new ApplyRecipeCommand("NV1", "op", "apply-" + run, at, Coater, "COATER", "NV-P120-NMC", "COAT",
            run, "ROLL-" + run), Ct);

    private static async Task Receive(ServiceProvider host, string lot, DateTimeOffset at, string uom) =>
        (await host.DispatchAsync(new ReceiveMaterialLotCommand("NV1", "store", "receive-" + lot, at, lot, "ELECTROLYTE",
            1000m, uom, T0.AddDays(90), 240, "SUP-" + lot), Ct)).Accepted.ShouldBeTrue();

    private static async Task Release(ServiceProvider host, string lot) =>
        (await host.DispatchAsync(new ReleaseMaterialLotCommand("NV1", "qa.eng", "release-" + lot, T0, lot), Ct))
            .Accepted.ShouldBeTrue();

    private static async Task Open(ServiceProvider host, string lot) =>
        (await host.DispatchAsync(new OpenMaterialLotCommand("NV1", "op", "open-" + lot, T0, lot), Ct)).Accepted.ShouldBeTrue();

    private static Task<DomainCommandResult> Consume(ServiceProvider host, string lot, string uom, string submission) =>
        host.DispatchAsync(new ConsumeMaterialCommand("NV1", "op", "consume-" + submission, T0, "NV1CL16071A07001", lot, "Lot",
            "ELECTROLYTE", 5m, uom, null, null, "RUN-FILL"), Ct);
}
