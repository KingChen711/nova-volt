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
using Nvm.PublicObjectModel;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

public sealed class PomEquipmentTests : IClassFixture<PomEquipmentFixture>
{
    private readonly PomEquipmentFixture _fixture;

    public PomEquipmentTests(PomEquipmentFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("Equipment")]
    [InlineData("$metadata")]
    [InlineData("")]
    public async Task EveryReadSurfaceRequiresAuthentication(string path)
    {
        using var response = await _fixture.SendAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("signature")]
    public async Task InvalidTokensAreRejected(string defect)
    {
        using var response = await _fixture.SendAsync("Equipment", _fixture.Token(defect: defect));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("", "Operator")]
    [InlineData("NV1,DE1", "Operator")]
    [InlineData("OTHER", "Operator")]
    [InlineData("NV1", "QaEngineer")]
    public async Task UnscopedOrUnauthorizedPrincipalsAreForbidden(string sites, string role)
    {
        using var response = await _fixture.SendAsync("Equipment", _fixture.Token(sites, role));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SitePredicateSurvivesClientFiltersCountsAndPrincipalChanges()
    {
        // Cùng EF model cache, request kế tiếp phải dùng site của principal mới.
        foreach (var site in new[] { "NV1", "DE1", "NV1" })
        {
            var other = site == "NV1" ? "DE1" : "NV1";
            var token = _fixture.Token(site, "LineLeader");
            using var response = await _fixture.SendAsync("Equipment?$count=true&$orderby=Name&$top=1000", token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            json.RootElement.GetProperty("@odata.count").GetInt32().ShouldBe(52);
            json.RootElement.GetProperty("value").EnumerateArray()
                .Select(row => row.GetProperty("SiteId").GetString()).Distinct().ShouldBe([site]);

            using var filtered = await _fixture.SendAsync($"Equipment?$filter=SiteId eq '{other}'&$count=true", token);
            filtered.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var empty = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            empty.RootElement.GetProperty("@odata.count").GetInt32().ShouldBe(0);
            empty.RootElement.GetProperty("value").GetArrayLength().ShouldBe(0);

            using var count = await _fixture.SendAsync("Equipment/$count", token);
            count.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await count.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("52");
            using var missing = await _fixture.SendAsync($"Equipment('{other}-001')", token);
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task ServerPagingHasNoGapsDuplicatesOrForeignSiteRows()
    {
        var ids = new List<string>();
        var next = "Equipment?$orderby=Name&$select=Id,SiteId&$count=true";
        var pages = 0;
        while (next is not null)
        {
            pages++;
            pages.ShouldBeLessThanOrEqualTo(2);
            using var response = await _fixture.SendAsync(next, _fixture.Token());
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var rows = json.RootElement.GetProperty("value");
            rows.GetArrayLength().ShouldBe(pages == 1 ? 50 : 2);
            foreach (var row in rows.EnumerateArray())
            {
                row.GetProperty("SiteId").GetString().ShouldBe("NV1");
                ids.Add(row.GetProperty("Id").GetString()!);
            }

            next = json.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }

        pages.ShouldBe(2);
        ids.Count.ShouldBe(52);
        ids.Distinct().Count().ShouldBe(52);
        using var clientPage = await _fixture.SendAsync("Equipment?$orderby=Id&$skip=2&$top=2", _fixture.Token());
        using var pageJson = JsonDocument.Parse(await clientPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        pageJson.RootElement.GetProperty("value").EnumerateArray().Select(row => row.GetProperty("Id").GetString())
            .ShouldBe(["NV1-003", "NV1-004"]);
    }

    [Fact]
    public async Task EntityEtagCannotTurnACrossSiteLookupIntoNotModified()
    {
        using var own = await _fixture.SendAsync("Equipment('NV1-001')", _fixture.Token());
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        own.Headers.ETag.ShouldNotBeNull();
        using var conditional = await _fixture.SendAsync("Equipment('NV1-001')", _fixture.Token(), etag: own.Headers.ETag.ToString());
        conditional.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        using var foreign = await _fixture.SendAsync("Equipment('NV1-001')", _fixture.Token("DE1"), etag: own.Headers.ETag.ToString());
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MetadataHasBoundedStringKeyAndNoNavigationOrActions()
    {
        using var response = await _fixture.SendAsync("$metadata", _fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var metadata = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var xml = XDocument.Parse(metadata);
        XNamespace edm = "http://docs.oasis-open.org/odata/ns/edm";
        var entity = xml.Descendants(edm + "EntityType").Single();
        entity.Element(edm + "Key")!.Element(edm + "PropertyRef")!.Attribute("Name")!.Value.ShouldBe("Id");
        var key = entity.Elements(edm + "Property").Single(property => property.Attribute("Name")!.Value == "Id");
        key.Attribute("Type")!.Value.ShouldBe("Edm.String");
        key.Attribute("MaxLength")!.Value.ShouldBe("64");
        xml.Descendants(edm + "NavigationProperty").ShouldBeEmpty();
        xml.Descendants(edm + "Action").ShouldBeEmpty();
        // Artifact dùng cho import PoC; đây là response có auth, không phải EDM viết tay.
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Equipment.metadata.actual.xml"),
            metadata, TestContext.Current.CancellationToken);
        using var expected = typeof(PomEquipmentTests).Assembly.GetManifestResourceStream("Equipment.metadata.xml")!;
        XNode.DeepEquals(xml, XDocument.Load(expected)).ShouldBeTrue("metadata import phải khớp response của host");
    }

    [Theory]
    [InlineData("Equipment?$top=1001")]
    [InlineData("Equipment?$expand=Genealogy")]
    [InlineData("Equipment?$apply=aggregate(Revision with sum as Total)")]
    public async Task UnsupportedOrUnboundedQueriesAreRejected(string query)
    {
        using var response = await _fixture.SendAsync(query, _fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("POST", "Equipment")]
    [InlineData("PATCH", "Equipment('NV1-001')")]
    [InlineData("DELETE", "Equipment('NV1-001')")]
    public async Task NoBusinessWriteEndpointExists(string method, string path)
    {
        using var response = await _fixture.SendAsync(path, _fixture.Token(), new HttpMethod(method));
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }
}

public sealed class PomEquipmentFixture : IAsyncLifetime
{
    private const string Issuer = "https://issuer.example/realms/novavolt";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.9-alpine").Build();
    private readonly RSA _signingKey = RSA.Create(2048);
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        PomSchemaMigrator.Upgrade(_postgres.GetConnectionString());
        PomSchemaMigrator.Upgrade(_postgres.GetConnectionString());
        await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO pom.equipment(id, site_id, equipment_path, name, line, resource, revision)
                SELECT site || '-' || lpad(n::text, 3, '0'), site,
                    'NOVAVOLT/' || site || '/PACK/P1/EOL-01', 'Same name', 'P1', 'EOL-01', 3
                FROM (VALUES ('NV1'), ('DE1')) s(site) CROSS JOIN generate_series(1, 52) n;
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NVM_POM:ConnectionString"] = _postgres.GetConnectionString(),
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

    public string Token(string sites = "NV1", string role = "Operator", string? defect = null)
    {
        var claims = new List<Claim> { new("sub", "test-operator"), new("mendix_roles", role) };
        claims.AddRange(sites.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(site => new Claim("site_id", site)));
        var now = TimeProvider.System.GetUtcNow();
        using var wrongKey = RSA.Create(2048);
        var token = new JwtSecurityToken(
            issuer: defect == "issuer" ? "https://other.example" : Issuer,
            audience: defect == "audience" ? "another-api" : "nvm-api",
            claims: claims,
            notBefore: now.AddHours(-1).UtcDateTime,
            expires: (defect == "expired" ? now.AddMinutes(-5) : now.AddMinutes(5)).UtcDateTime,
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(defect == "signature" ? wrongKey : _signingKey) { KeyId = "pom-test" }, SecurityAlgorithms.RsaSha256));
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
