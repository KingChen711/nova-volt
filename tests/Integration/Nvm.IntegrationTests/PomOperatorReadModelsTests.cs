using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Nvm.App.Execution;
using Nvm.Kernel.Identity;
using Nvm.PublicObjectModel;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// C03 read model tests cho ProductionUnits và WipBoard. Dùng PostgreSQL thật qua Testcontainers và
/// cùng generator fixture (<see cref="OperatorFixture"/>) mà seed dùng, không mock, không InMemory EF.
/// Không lặp lại hàng loạt case JWT của C02 — chỉ kiểm hành vi mới của hai entity set.
/// </summary>
public sealed class PomOperatorReadModelsTests : IClassFixture<PomOperatorFixture>
{
    private readonly PomOperatorFixture _fixture;

    public PomOperatorReadModelsTests(PomOperatorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MetadataIncludesAllThreeBoundedEntitiesWithStringKeys()
    {
        using var response = await _fixture.SendAsync("$metadata", _fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var metadata = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var xml = XDocument.Parse(metadata);
        XNamespace edm = "http://docs.oasis-open.org/odata/ns/edm";

        var sets = xml.Descendants(edm + "EntitySet").Select(set => set.Attribute("Name")!.Value).ToList();
        sets.ShouldBe(["Equipment", "ProductionUnits", "WipBoard"], ignoreOrder: true);

        // Mọi key là Edm.String có MaxLength hữu hạn; DTO phẳng nên không có navigation/action.
        foreach (var type in xml.Descendants(edm + "EntityType"))
        {
            var keyName = type.Element(edm + "Key")!.Element(edm + "PropertyRef")!.Attribute("Name")!.Value;
            var key = type.Elements(edm + "Property").Single(p => p.Attribute("Name")!.Value == keyName);
            key.Attribute("Type")!.Value.ShouldBe("Edm.String");
            key.Attribute("MaxLength").ShouldNotBeNull();
        }

        xml.Descendants(edm + "NavigationProperty").ShouldBeEmpty();
        xml.Descendants(edm + "Action").ShouldBeEmpty();
        xml.Descendants(edm + "Function").ShouldBeEmpty();

        // Snapshot dùng để import lại vào Studio Pro; đây là response có auth, không phải EDM viết tay.
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Pom.metadata.actual.xml"),
            metadata, TestContext.Current.CancellationToken);
        using var expected = typeof(PomOperatorReadModelsTests).Assembly.GetManifestResourceStream("Pom.metadata.xml")!;
        XNode.DeepEquals(xml, XDocument.Load(expected)).ShouldBeTrue("metadata import phải khớp response của host");
    }

    [Theory]
    [InlineData("ProductionUnits", 1000)]
    [InlineData("WipBoard", 20)]
    public async Task CountAndSitePredicateSurviveClientFiltersAndPrincipalChanges(string set, int nv1Count)
    {
        // Cùng EF model cache: đổi principal giữa các request phải đổi site theo principal mới.
        foreach (var site in new[] { "NV1", "DE1", "NV1" })
        {
            var expected = site == "NV1" ? nv1Count : (set == "WipBoard" ? 12 : 1000);
            var other = site == "NV1" ? "DE1" : "NV1";
            var token = _fixture.Token(site, "LineLeader");

            using var count = await _fixture.SendAsync($"{set}/$count", token);
            count.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await count.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .ShouldBe(expected.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Filter client gửi không thay được predicate site: xin site khác vẫn ra 0.
            using var foreign = await _fixture.SendAsync($"{set}?$filter=SiteId eq '{other}'&$count=true&$top=1000", token);
            foreign.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            json.RootElement.GetProperty("@odata.count").GetInt32().ShouldBe(0);
            json.RootElement.GetProperty("value").GetArrayLength().ShouldBe(0);
        }
    }

    [Fact]
    public async Task ProductionUnitsPagingHasNoGapsDuplicatesOrForeignSiteRows()
    {
        var ids = new List<string>();
        var next = "ProductionUnits?$orderby=QualityState&$select=Id,SiteId&$count=true";
        var pages = 0;
        while (next is not null)
        {
            pages++;
            pages.ShouldBeLessThanOrEqualTo(20);
            using var response = await _fixture.SendAsync(next, _fixture.Token());
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var rows = json.RootElement.GetProperty("value");
            rows.GetArrayLength().ShouldBe(50); // 1000 rows chia đều 20 trang, trang cuối vẫn đủ 50.
            foreach (var row in rows.EnumerateArray())
            {
                row.GetProperty("SiteId").GetString().ShouldBe("NV1");
                ids.Add(row.GetProperty("Id").GetString()!);
            }

            next = json.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }

        pages.ShouldBe(20); // 1000 rows / 50 per page; trang cuối không còn nextLink.
        ids.Count.ShouldBe(1000);
        ids.Distinct().Count().ShouldBe(1000);
    }

    [Fact]
    public async Task ProductionUnitTopSkipSelectAndFilterAreScoped()
    {
        using var page = await _fixture.SendAsync("ProductionUnits?$orderby=Id&$skip=1&$top=2&$select=Id,SerialNumber", _fixture.Token());
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var pageJson = JsonDocument.Parse(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var rows = pageJson.RootElement.GetProperty("value");
        rows.GetArrayLength().ShouldBe(2);
        // $select giới hạn thuộc tính trả về.
        rows[0].TryGetProperty("QualityState", out _).ShouldBeFalse();
        rows[0].GetProperty("Id").GetString().ShouldBe("NV1-U000002");

        // Case bị chặn có nghĩa: pack Held ở EOL của NV1 đúng 15.
        using var held = await _fixture.SendAsync(
            "ProductionUnits/$count?$filter=StepCode eq 'EOL' and QualityState eq 'Held'", _fixture.Token());
        held.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await held.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("15");
    }

    [Fact]
    public async Task HappyPathAndBlockedUnitsCarrySeparateExecutionQualityLocationState()
    {
        // Happy path §2.3: pack EOL, operation run Running, quality Pending, serial đã pin trong plan.
        using var response = await _fixture.SendAsync("ProductionUnits('NV1-U000851')", _fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var unit = json.RootElement;
        unit.GetProperty("SerialNumber").GetString().ShouldBe("NV1PP16250A00001");
        unit.GetProperty("OperationRunId").GetString().ShouldBe("OPRUN-NV1-EOL-0001");
        unit.GetProperty("EquipmentPath").GetString().ShouldBe("NOVAVOLT/NV1/PACK/P1/EOL-01");
        unit.GetProperty("ExecutionState").GetString().ShouldBe("Running");
        unit.GetProperty("QualityState").GetString().ShouldBe("Pending");
        unit.GetProperty("LocationState").GetString().ShouldBe("AtStation");
        unit.GetProperty("BlockingReasonCode").ValueKind.ShouldBe(JsonValueKind.Null);

        // Held pack: ba trục state khác nhau — execution vẫn Running, quality Held, location đã rời sang rack.
        using var blocked = await _fixture.SendAsync("ProductionUnits('NV1-U000857')", _fixture.Token());
        blocked.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var blockedJson = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var heldUnit = blockedJson.RootElement;
        heldUnit.GetProperty("ExecutionState").GetString().ShouldBe("Running");
        heldUnit.GetProperty("QualityState").GetString().ShouldBe("Held");
        heldUnit.GetProperty("LocationState").GetString().ShouldBe("AtRack-HOLD");
        heldUnit.GetProperty("BlockingReasonCode").GetString().ShouldBe("QUALITY_HOLD");
    }

    [Theory]
    [InlineData("ProductionUnits('NV1-U000851')", "ProductionUnits('DE1-U000001')")]
    [InlineData("WipBoard('NV1-P1-EOL-Pending')", "WipBoard('DE1-P1-EOL-Pending')")]
    public async Task KeyLookupIsScopedAndConditionalRequestsCannotCrossSites(string own, string foreign)
    {
        using var hit = await _fixture.SendAsync(own, _fixture.Token());
        hit.StatusCode.ShouldBe(HttpStatusCode.OK);
        hit.Headers.ETag.ShouldNotBeNull();

        using var notModified = await _fixture.SendAsync(own, _fixture.Token(), etag: hit.Headers.ETag.ToString());
        notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);

        // Key của site khác không tồn tại với principal NV1: 404, không phải 304.
        using var crossKey = await _fixture.SendAsync(foreign, _fixture.Token());
        crossKey.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // ETag của mình không được biến một lookup site khác thành 304.
        using var crossConditional = await _fixture.SendAsync(own, _fixture.Token("DE1"), etag: hit.Headers.ETag.ToString());
        crossConditional.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("ProductionUnits?$top=1001")]
    [InlineData("ProductionUnits?$expand=Measurements")]
    [InlineData("ProductionUnits?$apply=aggregate(Revision with sum as Total)")]
    [InlineData("WipBoard?$top=1001")]
    [InlineData("WipBoard?$apply=groupby((StepCode))")]
    public async Task UnsupportedOrUnboundedQueriesAreRejected(string query)
    {
        using var response = await _fixture.SendAsync(query, _fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("POST", "ProductionUnits")]
    [InlineData("PATCH", "ProductionUnits('NV1-U000851')")]
    [InlineData("DELETE", "ProductionUnits('NV1-U000851')")]
    [InlineData("POST", "WipBoard")]
    [InlineData("DELETE", "WipBoard('NV1-P1-EOL-Pending')")]
    public async Task NoBusinessWriteEndpointExists(string method, string path)
    {
        using var response = await _fixture.SendAsync(path, _fixture.Token(), new HttpMethod(method));
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Theory]
    [InlineData("NV1")]
    [InlineData("DE1")]
    public async Task WipBoardCountsReconcileWithProductionUnits(string site)
    {
        var token = _fixture.Token(site);

        // Tổng các dòng WIP của một site bằng đúng số ProductionUnits của site đó.
        using var wip = await _fixture.SendAsync("WipBoard?$select=UnitCount&$top=1000", token);
        using var wipJson = JsonDocument.Parse(await wip.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var wipTotal = wipJson.RootElement.GetProperty("value").EnumerateArray()
            .Sum(row => row.GetProperty("UnitCount").GetInt32());
        wipTotal.ShouldBe(1000);

        // Một nhóm cụ thể: EOL/Pending. WIP count phải bằng $count của ProductionUnits cùng điều kiện.
        using var group = await _fixture.SendAsync(
            "WipBoard?$filter=StepCode eq 'EOL' and QualityState eq 'Pending'&$select=UnitCount", token);
        using var groupJson = JsonDocument.Parse(await group.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var groupRow = groupJson.RootElement.GetProperty("value").EnumerateArray().Single();
        var wipGroup = groupRow.GetProperty("UnitCount").GetInt32();

        using var unitCount = await _fixture.SendAsync(
            "ProductionUnits/$count?$filter=StepCode eq 'EOL' and QualityState eq 'Pending'", token);
        var units = int.Parse(await unitCount.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        wipGroup.ShouldBe(units);
        wipGroup.ShouldBe(site == "NV1" ? 105 : 210);
    }

    [Fact]
    public async Task ReseedingDoesNotOverwriteExistingRows()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // Đổi một giá trị đã lưu: seed phải giữ nó, không chỉ giữ row count.
        await using var change = new NpgsqlCommand("""
            UPDATE pom.production_units SET blocking_reason_text = 'Preserve this existing context'
            WHERE site_id = 'NV1' AND id = 'NV1-U000857';
            """, connection);
        await change.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        try
        {
            await PomFixtureSeed.PrepareAsync(_fixture.SeedConfiguration, includeOperators: true);
            await using var preserved = new NpgsqlCommand("""
                SELECT blocking_reason_text FROM pom.production_units WHERE site_id = 'NV1' AND id = 'NV1-U000857';
                """, connection);
            (await preserved.ExecuteScalarAsync(TestContext.Current.CancellationToken))
                .ShouldBe("Preserve this existing context");
            // Đổi credential khi seed lại sẽ khiến kết nối runtime hiện có mất quyền mở connection mới.
            using var response = await _fixture.SendAsync("ProductionUnits/$count", _fixture.Token());
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            change.CommandText = """
                UPDATE pom.production_units SET blocking_reason_text = 'Unit đang bị giữ chất lượng; thao tác sản xuất bị chặn.'
                WHERE site_id = 'NV1' AND id = 'NV1-U000857';
                """;
            await change.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var command = new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM pom.production_units), (SELECT count(*) FROM pom.wip_board);", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        reader.GetInt64(0).ShouldBe(2000); // 1000/site × 2 site.
        reader.GetInt64(1).ShouldBe(32);   // NV1 20 + DE1 12 nhóm.
    }

    [Fact]
    public async Task StoredFixtureMatchesSerialIdentityEquipmentAndEveryWipGroup()
    {
        var units = OperatorFixture.GenerateUnits();
        units.ShouldBe(OperatorFixture.GenerateUnits());
        units.Select(unit => unit.SerialNumber).Distinct().Count().ShouldBe(2000);
        foreach (var unit in units)
        {
            var serial = SerialNumber.Parse(unit.SerialNumber);
            serial.SiteCode.ShouldBe(unit.SiteId);
            serial.Kind.ToString().ShouldBe(unit.UnitKind);
            unit.Revision.ShouldBe(3);
            unit.LocationState.ShouldNotBe("Shipped");
            if (unit.StepCode == "FORM")
            {
                unit.SiteId.ShouldBe("NV1");
                unit.Line.ShouldBe("F1");
                serial.LineCode.ShouldBe("L1");
            }

            if (unit.ExecutionState != "Running" || unit.QualityState is "Held" or "Scrapped")
            {
                unit.BlockingReasonCode.ShouldNotBeNullOrEmpty();
                unit.BlockingReasonText.ShouldNotBeNullOrEmpty();
            }
        }

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var check = new NpgsqlCommand("""
            SELECT count(*) FROM pom.production_units u
            LEFT JOIN pom.equipment e ON e.site_id = u.site_id AND e.equipment_path = u.equipment_path
                AND e.line = u.line AND e.resource = u.resource AND e.revision = u.revision
            WHERE e.id IS NULL;
            """, connection);
        (await check.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(0L);
        check.CommandText = """
            SELECT count(*) FROM (
                SELECT site_id, line, step_code, quality_state, count(*) AS unit_count
                FROM pom.production_units GROUP BY site_id, line, step_code, quality_state
            ) u FULL JOIN pom.wip_board w USING (site_id, line, step_code, quality_state)
            WHERE u.unit_count IS DISTINCT FROM w.unit_count;
            """;
        (await check.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(0L);
        check.CommandText = """
            SELECT bool_and(has_table_privilege('nvm_pom', tablename, 'SELECT')
                AND NOT has_table_privilege('nvm_pom', tablename, 'INSERT,UPDATE,DELETE'))
            FROM (VALUES ('pom.production_units'), ('pom.wip_board'), ('pom.equipment')) t(tablename);
            """;
        (await check.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(true);
    }

    [Fact]
    public async Task WipPagingResolvesTiesAndStopsAtTheFinalPage()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        // Snapshot có 20 nhóm NV1; thêm nhóm test riêng vượt 50 để bác bỏ paging chỉ trả trang đầu.
        await using var command = new NpgsqlCommand("""
            INSERT INTO pom.wip_board(id, site_id, line, step_code, quality_state, unit_count, revision)
            SELECT 'TEST-' || site || '-' || lpad(n::text, 3, '0'), site, 'T1', 'TEST', 'Pending', 1, 3
            FROM (VALUES ('NV1'), ('DE1')) s(site) CROSS JOIN generate_series(1, 55) n;
            """, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        try
        {
            var ids = new List<string>();
            var next = "WipBoard?$filter=StepCode eq 'TEST'&$orderby=UnitCount&$select=Id,SiteId&$count=true";
            var pages = 0;
            while (next is not null)
            {
                (++pages).ShouldBeLessThanOrEqualTo(2);
                using var response = await _fixture.SendAsync(next, _fixture.Token());
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                json.RootElement.GetProperty("@odata.count").GetInt32().ShouldBe(55);
                var rows = json.RootElement.GetProperty("value");
                rows.GetArrayLength().ShouldBe(pages == 1 ? 50 : 5);
                foreach (var row in rows.EnumerateArray())
                {
                    row.GetProperty("SiteId").GetString().ShouldBe("NV1");
                    ids.Add(row.GetProperty("Id").GetString()!);
                }

                next = json.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            }

            ids.ShouldBe(Enumerable.Range(1, 55).Select(i => FormattableString.Invariant($"TEST-NV1-{i:000}")));
            using var slice = await _fixture.SendAsync(
                "WipBoard?$filter=StepCode eq 'TEST'&$orderby=UnitCount&$skip=50&$top=2&$select=Id", _fixture.Token());
            using var sliceJson = JsonDocument.Parse(await slice.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            sliceJson.RootElement.GetProperty("value").EnumerateArray().Select(row => row.GetProperty("Id").GetString())
                .ShouldBe(["TEST-NV1-051", "TEST-NV1-052"]);
            sliceJson.RootElement.TryGetProperty("@odata.nextLink", out _).ShouldBeFalse();
        }
        finally
        {
            command.CommandText = "DELETE FROM pom.wip_board WHERE site_id IN ('NV1', 'DE1') AND step_code = 'TEST';";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
}

public sealed class PomOperatorFixture : IAsyncLifetime
{
    private const string Issuer = "https://issuer.example/realms/novavolt";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.9-alpine").Build();
    private readonly RSA _signingKey = RSA.Create(2048);
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public IConfiguration SeedConfiguration => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["NVM_POM:MigrationConnectionString"] = ConnectionString,
        ["NVM_POM:FactoryModelPath"] = Path.Combine(AppContext.BaseDirectory, "factory-model.r3.json"),
        ["NVM_POM_PASSWORD"] = _readerPassword,
    }).Build();

    private readonly string _readerPassword = Guid.NewGuid().ToString("N");

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await PomFixtureSeed.PrepareAsync(SeedConfiguration, includeOperators: true);
        var readerConnection = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Username = "nvm_pom",
            Password = _readerPassword,
        }.ConnectionString;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NVM_POM:ConnectionString"] = readerConnection,
            ["NVM_POM:Authority"] = Issuer,
        });
        builder.Services.AddNvmPublicObjectModel(builder.Configuration, builder.Environment);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(new RsaSecurityKey(_signingKey) { KeyId = "pom-test" });
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        });
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapNvmPublicObjectModel();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public string Token(string sites = "NV1", string role = "Operator")
    {
        var claims = new List<Claim> { new("sub", "test-operator"), new("mendix_roles", role) };
        claims.AddRange(sites.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(site => new Claim("site_id", site)));
        var now = TimeProvider.System.GetUtcNow();
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: "nvm-api",
            claims: claims,
            notBefore: now.AddHours(-1).UtcDateTime,
            expires: now.AddMinutes(5).UtcDateTime,
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(_signingKey) { KeyId = "pom-test" }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<HttpResponseMessage> SendAsync(string path, string? token = null, HttpMethod? method = null, string? etag = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get,
            path.StartsWith("http", StringComparison.Ordinal) ? path : "/pom/v1/" + path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (etag is not null)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
        }

        return await _client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _signingKey.Dispose();
        await _postgres.DisposeAsync();
    }
}
