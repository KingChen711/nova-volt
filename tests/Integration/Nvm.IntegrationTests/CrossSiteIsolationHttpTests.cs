using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;
using Nvm.Projections;

namespace Nvm.IntegrationTests;

/// <summary>
/// M10 ★ (scope §5.6): DE1 có dữ liệu thật ở mọi read endpoint; người dùng NV1 gọi đúng các endpoint đó và nhận
/// <b>0</b> dòng DE1. Mỗi endpoint có đối chứng: token DE1 thấy dữ liệu của mình, để "rỗng" không phải vì chưa seed.
/// POM OData đã có test riêng với dữ liệu hai site (<c>PomEquipmentTests</c>, <c>PomOperatorReadModelsTests</c>).
/// </summary>
public sealed class CrossSiteIsolationHttpTests(ExecutionCommandHttpFixture fixture) : IClassFixture<ExecutionCommandHttpFixture>
{
    private const string De1Cell = "DE1CL16071A00001";
    private const string De1Module = "DE1MM16071A00001";
    private const string De1Coater = "NOVAVOLT/DE1/ELECTRODE/C1/COAT-01";
    private const string Nv1Stacker = "NOVAVOLT/NV1/CELL/L9/STACK-09";
    private const string De1Stacker = "NOVAVOLT/DE1/CELL/L9/STACK-09";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nv1User_SeesZeroDe1Rows_OnEveryReadEndpoint_WhileDe1SeesItsOwn()
    {
        var now = TimeProvider.System.GetUtcNow();   // app thật chạy đồng hồ hệ thống; aging/due so với giờ thật
        await SeedAsync(now);
        var nv1 = fixture.Token("NV1", "LineLeader", "leader-nv1");
        var de1 = fixture.Token("DE1", "LineLeader", "leader-de1");

        // Dashboard thiết bị/OEE.
        (await Text("/api/v1/equipment", de1)).ShouldContain(De1Stacker);
        var equipment = await Text("/api/v1/equipment", nv1);
        equipment.ShouldContain(Nv1Stacker);
        equipment.ShouldNotContain("DE1");
        var window = $"from={Uri.EscapeDataString(now.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddHours(1).ToString("O"))}";
        (await Text("/api/v1/oee?" + window, de1)).ShouldContain(De1Stacker);
        (await Text("/api/v1/oee?" + window, nv1)).ShouldNotContain("DE1");
        (await Text($"/api/v1/oee?{window}&equipment={Uri.EscapeDataString(De1Stacker)}", nv1)).ShouldNotContain("DE1");

        // Quality: hold và SPC của DE1.
        var holdId = await PlaceDe1HoldAsync(now);
        (await Status($"/api/v1/quality/holds/{holdId}", de1)).ShouldBe(HttpStatusCode.OK);
        (await Status($"/api/v1/quality/holds/{holdId}", nv1)).ShouldBe(HttpStatusCode.NotFound);
        (await Json("/api/v1/quality/spc/DE1-WELD-PULL", de1)).GetProperty("subgroups").GetArrayLength().ShouldBe(2);
        (await Json("/api/v1/quality/spc/DE1-WELD-PULL", nv1)).GetProperty("subgroups").GetArrayLength().ShouldBe(0);

        // Recipe đã chạy trên máy DE1.
        var at = Uri.EscapeDataString(now.ToString("O"));
        (await Status($"/api/v1/recipes/applied?equipmentPath={Uri.EscapeDataString(De1Coater)}&at={at}", de1)).ShouldBe(HttpStatusCode.OK);
        (await Status($"/api/v1/recipes/applied?equipmentPath={Uri.EscapeDataString(De1Coater)}&at={at}", nv1)).ShouldBe(HttpStatusCode.NotFound);

        // Kho aging.
        (await Text("/api/v1/aging/due?withinHours=48", de1)).ShouldContain(De1Cell);
        var due = await Text("/api/v1/aging/due?withinHours=48", nv1);
        due.ShouldContain("NV1CL16071A00001");
        due.ShouldNotContain("DE1");
        (await Text("/api/v1/aging/racks/R01/levels/1", nv1)).ShouldNotContain("DE1");

        // Trace: gốc DE1 hỏi bằng token NV1 không có node nào.
        (await Json($"/api/v1/trace/forward/cell/{De1Cell}", de1)).GetProperty("nodes").GetArrayLength().ShouldBe(1);
        (await Json($"/api/v1/trace/forward/cell/{De1Cell}", nv1)).GetProperty("nodes").GetArrayLength().ShouldBe(0);
        (await Json($"/api/v1/trace/backward/module/{De1Module}", nv1)).GetProperty("nodes").GetArrayLength().ShouldBe(0);

        // Kho bin cho matching.
        (await Text("/api/v1/matching/bins/NV-P120-NMC", de1)).ShouldContain("BIN-DE1-ONLY");
        (await Text("/api/v1/matching/bins/NV-P120-NMC", nv1)).ShouldNotContain("BIN-DE1-ONLY");
    }

    private async Task SeedAsync(DateTimeOffset now)
    {
        await using (var connection = new SqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            using var command = new SqlCommand("""
                DELETE FROM execution.FormationAging WHERE SerialNumber IN ('DE1CL16071A00001', 'NV1CL16071A00001');
                INSERT INTO execution.FormationAging (SiteId, SerialNumber, State, Version, TrayId, Channel, EquipmentPath,
                    FormationDueAt, Ocv1Millivolt, RackId, Level, AgingChannel, AgingDueAt, UpdatedAt)
                VALUES ('DE1', 'DE1CL16071A00001', 'Aging', 3, 'TRAY-DE1', 1, 'NOVAVOLT/DE1/FORMATION/F1/FORM-01', @now, 3650,
                        'R01', 1, 1, @due, @now),
                       ('NV1', 'NV1CL16071A00001', 'Aging', 3, 'TRAY-NV1', 1, 'NOVAVOLT/NV1/FORMATION/F1/FORM-01', @now, 3650,
                        'R01', 1, 2, @due, @now);
                INSERT INTO recipe.Applications (SiteId, EventId, EquipmentPath, AppliedAt, RecipeId, Version, ContentSha256,
                    OperationRunId, LotId)
                VALUES ('DE1', NEWID(), @coater, @applied, 'RCP-COAT-DE1', 1, REPLICATE('a', 64), 'RUN-DE1', NULL);
                """, connection);
            command.Parameters.AddWithValue("@now", now);
            command.Parameters.AddWithValue("@due", now.AddHours(12));
            command.Parameters.AddWithValue("@applied", now.AddHours(-2));
            command.Parameters.AddWithValue("@coater", De1Coater);
            await command.ExecuteNonQueryAsync(Ct);
        }
        await using (var data = NpgsqlDataSource.Create(fixture.PostgresConnectionString))
        {
            await ProjectionSchemaMigrator.UpgradeAsync(data, Ct);
            await using var command = data.CreateCommand("""
                INSERT INTO trace.genealogy_link (site_id, edge_kind, parent_type, parent_id, child_type, child_id,
                    operation_run_id, linked_at, recorded_at, source_event_id)
                VALUES ('DE1', 2, 2, 'DE1CL16071A00001', 3, 'DE1MM16071A00001', 'RUN-DE1', now(), now(), gen_random_uuid());
                INSERT INTO rm.genealogy_closure (site_id, ancestor_type, ancestor_id, descendant_type, descendant_id, paths)
                VALUES ('DE1', 2, 'DE1CL16071A00001', 3, 'DE1MM16071A00001', 1) ON CONFLICT DO NOTHING;
                INSERT INTO rm.bin_inventory (site_id, serial_number, product_code, bin_code)
                VALUES ('DE1', 'DE1CL16071A00002', 'NV-P120-NMC', 'BIN-DE1-ONLY') ON CONFLICT DO NOTHING;
                """);
            await command.ExecuteNonQueryAsync(Ct);
        }
        foreach (var (site, path) in new[] { ("NV1", Nv1Stacker), ("DE1", De1Stacker) })
        {
            using var response = await PostAsync("/api/v1/commands/equipment/register", site, "ProductionManager", now.AddMinutes(-30),
                new { submissionId = "iso-" + path, equipmentPath = path, equipmentClass = "STACKER" });
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        }
        foreach (var subgroup in new[] { "SG-1", "SG-2" })
        {
            using var response = await PostAsync("/api/v1/commands/quality/record-spc-sample", "DE1", "LineLeader", now,
                new
                {
                    submissionId = "iso-spc-" + subgroup,
                    characteristic = "DE1-WELD-PULL",
                    subgroupId = subgroup,
                    values = new[] { 41.2m, 40.8m, 41.5m }
                });
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        }
    }

    private async Task<string> PlaceDe1HoldAsync(DateTimeOffset now)
    {
        using var response = await PostAsync("/api/v1/commands/quality/place-hold", "DE1", "QaEngineer", now,
            new { submissionId = "iso-hold", targetKind = "Lot", targetId = "LOT-DE1-ISO", reasonCode = "SUPPLIER_CAR" });
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("reasonText").GetString()!;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string site, string role, DateTimeOffset occurredAt, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { siteId = site, occurredAt, payload })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(site, role, $"{role}-{site}"));
        return await fixture.Client.SendAsync(request, Ct);
    }

    private async Task<HttpStatusCode> Status(string path, string token)
    {
        using var response = await Get(path, token);
        return response.StatusCode;
    }

    private async Task<string> Text(string path, string token)
    {
        using var response = await Get(path, token);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{path}: {body}");
        return body;
    }

    private async Task<JsonElement> Json(string path, string token)
    {
        using var document = JsonDocument.Parse(await Text(path, token));
        return document.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> Get(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await fixture.Client.SendAsync(request, Ct);
    }
}
