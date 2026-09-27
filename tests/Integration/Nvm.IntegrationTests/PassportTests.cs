using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Kernel.Commands;
using Nvm.Material.Commands;
using Nvm.Passport.Commands;
using Nvm.Passport.Entities;
using Nvm.Passport.Hosting;
using Nvm.ProductionExecution.Commands;
using Nvm.Projections;
using Nvm.Quality.Commands;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// M12: passport dựng từ genealogy thật, công bố bằng chữ ký ComplianceOwner, bất biến ở DB, công bố lại là version mới;
/// regulator đọc có audit; sản phẩm B không có passport nhưng có carbon footprint.
/// </summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class PassportTests(SqlCommandStoreFixture sql) : IClassFixture<SqlCommandStoreFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string ProductA = "NV-P120-NMC";
    private const string ProductB = "NV-C100-LFP-CTP";
    private const string GtinA = "09506000134352";
    private const string GtinB = "09506000134369";
    private const string Pack = "NV1PP16238A00042";
    private const string PackB = "NV1PP16238A00043";
    private const string Module = "NV1MM16238A00042";
    private const string Cell = "NV1CL16238A00042";

    [Fact]
    public async Task PassportFromGenealogy_PublishedBySignature_ImmutableVersioned_AndAuditedForRegulator()
    {
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        await AddPackRoutesAsync();
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(Ct);
        await using var data = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await ProjectionSchemaMigrator.UpgradeAsync(data, Ct);
        var clock = new FakeTimeProvider(T0);
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock, data);

        await Ok(host, Serialize(Cell, TraceabilityFixtureSeed.ProductCode));
        await Ok(host, Serialize(Module, TraceabilityFixtureSeed.PackProductCode));
        await Ok(host, Serialize(Pack, ProductA));
        await Ok(host, Serialize(PackB, ProductB));
        await Ok(host, new RecordRollCoatedCommand("NV1", "op", "coat", T0, "ROL-NV1-260825-CT1-004",
            [new("A", 0m, 3000m, "SLU-NV1-260825-MX2-07", "FOIL-1", "RCP-COAT v2", "COAT-01")]));
        await Ok(host, new ConsumeMaterialCommand("NV1", "op", "use", T0, Cell, "ROL-NV1-260825-CT1-004", "Roll", "ANODE", 0.82m, "m",
            1250m, 1250.82m, "RUN"));
        await Ok(host, new AssembleUnitCommand("NV1", "op", "asm-cell", Cell, T0, Module, "S01", "RUN"));
        await Ok(host, new AssembleUnitCommand("NV1", "op", "asm-module", Module, T0, Pack, "S01", "RUN"));
        await new GenealogyProjection(data, new SqlGlobalEventFeed(sql.ConnectionString)).CatchUpAsync("NV1", cancellationToken: Ct);

        await Ok(host, Model(GtinA, ProductA, requiresPassport: true));
        await Ok(host, Model(GtinB, ProductB, requiresPassport: false));
        (await host.DispatchAsync(Prepare("no-carbon"), Ct)).ReasonCode.ShouldBe(PassportReasonCodes.MissingEvidence);
        await Ok(host, Carbon(ProductA, 61.5m));
        await Ok(host, Carbon(ProductB, 38.2m));

        var draft = await host.DispatchAsync(Prepare("v1"), Ct);
        draft.Accepted.ShouldBeTrue(draft.ReasonCode);
        draft.StreamVersion.ShouldBe(1);
        (await host.DispatchAsync(Prepare("v1-again"), Ct)).ReasonCode.ShouldBe(PassportReasonCodes.DraftExists);
        var sha1 = draft.ReasonText!;
        (await host.DispatchAsync(Publish(1, "self", await Sign(host, "compliance.prep", 1, sha1)), Ct))
            .ReasonCode.ShouldBe("SEPARATION_OF_DUTIES");
        await Ok(host, Publish(1, "v1", await Sign(host, "compliance.officer", 1, sha1)));
        (await host.DispatchAsync(Publish(1, "v1-twice", await Sign(host, "compliance.officer2", 1, sha1)), Ct))
            .ReasonCode.ShouldBe(PassportReasonCodes.AlreadyPublished);

        // DB giữ bất biến kể cả khi có người UPDATE thẳng.
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            using var tamper = new SqlCommand("""
                UPDATE passport.Passports SET ContentJson = REPLACE(ContentJson, 'NMC811', 'LFP') WHERE SerialNumber = @serial;
                """, connection);
            tamper.Parameters.AddWithValue("@serial", Pack);
            (await Should.ThrowAsync<SqlException>(() => tamper.ExecuteNonQueryAsync(Ct))).Number.ShouldBe(51000);
        }

        var reader = host.GetRequiredService<PassportReader>();
        var publicView = (await reader.ReadAsync(GtinA, Pack, null, Audiences.Public, false, "anonymous", Ct)).View!;
        publicView.Version.ShouldBe(1);
        publicView.Fields["chemistry"].ShouldBe("NMC811");
        publicView.Fields["manufactureDate"].ShouldBe("2026-08-26");
        publicView.Fields.ShouldNotContainKey("genealogy");
        var regulator = (await reader.ReadAsync(GtinA, Pack, null, Audiences.Regulator, false, "reg.eu", Ct)).View!;
        JsonSerializer.Deserialize<string[]>(regulator.Fields["genealogy"])!.ShouldBe([$"cell:{Cell}", $"module:{Module}"]);
        JsonSerializer.Deserialize<string[]>(regulator.Fields["supplierLots"])!.ShouldBe(["roll:ROL-NV1-260825-CT1-004"]);
        JsonSerializer.Deserialize<string[]>(regulator.Fields["recipeVersions"])!.ShouldBe(["RCP-COAT v2"]);
        (await reader.ReadAsync(GtinB, Pack, null, Audiences.Public, false, "anonymous", Ct)).StatusCode.ShouldBe(404);

        // Công bố lại: version mới trỏ về version cũ; version cũ vẫn đọc được nguyên vẹn.
        await Ok(host, Carbon(ProductA, 58.9m, "carbon-a-2"));
        var second = await host.DispatchAsync(Prepare("v2"), Ct);
        second.StreamVersion.ShouldBe(2);
        await Ok(host, Publish(2, "v2", await Sign(host, "compliance.officer", 2, second.ReasonText!)));
        var latest = (await reader.ReadAsync(GtinA, Pack, null, Audiences.Public, false, "anonymous", Ct)).View!;
        latest.ShouldSatisfyAllConditions(v => v.Version.ShouldBe(2), v => v.PreviousVersion.ShouldBe(1),
            v => v.Fields["carbonFootprintKgCo2ePerKwh"].ShouldBe("58.9"));
        (await reader.ReadAsync(GtinA, Pack, 1, Audiences.Public, false, "anonymous", Ct)).View!
            .Fields["carbonFootprintKgCo2ePerKwh"].ShouldBe("61.5");
        await reader.ReadAsync(GtinA, Pack, 1, Audiences.Regulator, false, "reg.eu", Ct);

        var audit = await reader.AuditAsync("NV1", Pack, Ct);
        audit.Select(a => (a.Version, a.Subject)).ShouldBe([(1, "reg.eu"), (1, "reg.eu")]);   // public không ghi audit

        // Sản phẩm B không xuất EU: không passport, nhưng carbon footprint vẫn có.
        (await host.DispatchAsync(new PreparePassportCommand("NV1", "compliance.prep", "prepare-b", T0, PackB), Ct))
            .ReasonCode.ShouldBe(PassportReasonCodes.PassportNotRequired);
        (await reader.CarbonAsync("NV1", ProductB, 2026, Ct))!.KgCo2ePerKwh.ShouldBe(38.2m);
        (await reader.ReadAsync(GtinB, PackB, null, Audiences.Regulator, false, "reg.eu", Ct)).StatusCode.ShouldBe(404);
    }

    private async Task AddPackRoutesAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(Ct);
        using var command = new SqlCommand("""
            INSERT INTO traceability.Routes (SiteId, ProductCode, RoutingVersion, StepsJson, TransitionsJson)
            SELECT 'NV1', p.ProductCode, 'r1', '[{"code":"EOL"}]',
                '[{"action":"StartStep","from":0,"to":1},{"action":"CompleteStep","from":1,"to":2}]'
            FROM (VALUES (N'NV-P120-NMC'), (N'NV-C100-LFP-CTP')) p(ProductCode)
            WHERE NOT EXISTS (SELECT 1 FROM traceability.Routes r WHERE r.SiteId = 'NV1' AND r.ProductCode = p.ProductCode);
            """, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static SerializeUnitCommand Serialize(string serial, string product) =>
        new("NV1", "op", "birth-" + serial, serial, T0, product, "WO-DPP", TraceabilityFixtureSeed.RoutingVersion);

    private static DefineBatteryModelCommand Model(string gtin, string product, bool requiresPassport) =>
        new("NV1", "compliance.prep", "model-" + product, T0, gtin, product, "NovaVolt", requiresPassport ? "NMC811" : "LFP",
            requiresPassport ? 120m : 100m, requiresPassport ? 3000 : 6000, requiresPassport,
            """{"cathode":"NMC811","anode":"graphite"}""", "https://dpp.novavolt.example/docs/dismantling",
            "https://dpp.novavolt.example/docs/safety");

    private static RecordCarbonFootprintCommand Carbon(string product, decimal value, string submission = "") =>
        new("NV1", "compliance.prep", submission.Length > 0 ? submission : "carbon-" + product, T0, product, 2026, value,
            ImmutableDictionary<string, decimal>.Empty.Add("cobalt", 16m).Add("lithium", 6m), "TÜV (giả định)");

    private static PreparePassportCommand Prepare(string submission) =>
        new("NV1", "compliance.prep", "prepare-" + submission, T0, Pack);

    private static PublishPassportCommand Publish(int version, string submission, string signatureId) =>
        new("NV1", "compliance.prep", "publish-" + submission, T0, Pack, version, [signatureId]);

    private static async Task<string> Sign(ServiceProvider host, string actor, int version, string sha)
    {
        var result = await host.DispatchAsync(new SignCommand("NV1", actor, $"sign-{actor}-{version}", T0, "Passport",
            $"{Pack}:v{version}", "Approved", "ComplianceOwner", sha, null), Ct);
        result.Accepted.ShouldBeTrue(result.ReasonCode);
        return result.ReasonText!;
    }

    private static async Task Ok<TResult>(ServiceProvider host, ICommand<TResult> command)
    {
        var result = await host.DispatchAsync(command, Ct);
        switch (result)
        {
            case DomainCommandResult domain:
                domain.Accepted.ShouldBeTrue($"{command.GetType().Name}: {domain.ReasonCode} {domain.ReasonText}");
                break;
            case UnitCommandResult unit:
                unit.Accepted.ShouldBeTrue($"{command.GetType().Name}: {unit.ReasonCode}");
                break;
        }
    }
}
