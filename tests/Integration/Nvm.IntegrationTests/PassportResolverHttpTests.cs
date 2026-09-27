using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.Passport.Entities;
using Nvm.Passport.Hosting;

namespace Nvm.IntegrationTests;

/// <summary>M12: quét QR (URL GS1 Digital Link) qua app thật; token chỉ đổi tập trường, regulator có audit.</summary>
public sealed class PassportResolverHttpTests(ExecutionCommandHttpFixture fixture) : IClassFixture<ExecutionCommandHttpFixture>
{
    private const string Gtin = "09506000134352";
    private const string Serial = "NV1PP16238A00077";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Gs1Link_ReturnsThePublishedPassport_FilteredByAudience_AndAuditsTheRegulator()
    {
        await PublishAsync();
        var url = $"/01/{Gtin}/21/{Serial}";

        var anonymous = await GetAsync(url, null);
        anonymous.Status.ShouldBe(HttpStatusCode.OK);
        var publicFields = Fields(anonymous.Body);
        publicFields.ShouldContain("serialNumber");
        publicFields.ShouldContain("carbonFootprintKgCo2ePerKwh");
        publicFields.ShouldNotContain("genealogy");
        publicFields.ShouldNotContain("supplierLots");
        publicFields.ShouldNotContain("stateOfHealthPercent");
        anonymous.Body.ShouldNotContain("ROL-NV1-260825-CT1-004");

        var auditBefore = await AuditCountAsync();
        var regulator = await GetAsync(url, Token("regulator", "reg.eu"));
        Fields(regulator.Body).ShouldContain("genealogy");
        regulator.Body.ShouldContain("ROL-NV1-260825-CT1-004");
        (await AuditCountAsync()).ShouldBe(auditBefore + 1);

        Fields((await GetAsync(url, Token("consumer", "owner-1", new Claim(PassportRegistration.OwnerClaim, Serial)))).Body)
            .ShouldContain("stateOfHealthPercent");
        Fields((await GetAsync(url, Token("consumer", "someone-else"))).Body).ShouldNotContain("stateOfHealthPercent");
        (await GetAsync(url, Token("regulator", "confused", new Claim(PassportRegistration.AudienceClaim, "public")))).Status
            .ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(url, Token("auditor", "unknown"))).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync($"/01/09506000134353/21/{Serial}", null)).Status.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync($"/01/{Gtin}/21/NV1PP16238A09999", null)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task PublishAsync()
    {
        var document = PassportProjection.Build("NV1",
            new BatteryModel(Gtin, "NV-P120-NMC", "NovaVolt", "NMC811", 120m, 3000, true, """{"cathode":"NMC811"}""",
                "https://dpp.novavolt.example/docs/dismantling", "https://dpp.novavolt.example/docs/safety"),
            new CarbonFootprint("NV-P120-NMC", 2026, 1, 61.5m, ImmutableDictionary<string, decimal>.Empty.Add("cobalt", 16m), "TÜV"),
            new PackEvidence(Serial, "NV-P120-NMC", "Released", ["module:NV1MM16238A00077"], ["roll:ROL-NV1-260825-CT1-004"],
                ["RCP-COAT v2"]),
            new DateOnly(2026, 8, 26), 1, null);
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        using var command = new SqlCommand("""
            IF NOT EXISTS (SELECT 1 FROM passport.Passports WHERE SiteId = 'NV1' AND SerialNumber = @serial)
            INSERT INTO passport.Passports (SiteId, SerialNumber, Version, Gtin, PreviousVersion, ContentJson, ContentSha256, Status,
                PreparedBy, PreparedAt, SignatureIdsJson, PublishedBy, PublishedAt)
            VALUES ('NV1', @serial, 1, @gtin, NULL, @content, @sha, 'Published', 'compliance.prep', SYSDATETIMEOFFSET(),
                '["SIG-0000000000000001"]', 'compliance.officer', SYSDATETIMEOFFSET());
            """, connection);
        command.Parameters.AddWithValue("@serial", Serial);
        command.Parameters.AddWithValue("@gtin", Gtin);
        command.Parameters.AddWithValue("@content", document.ToJson());
        command.Parameters.AddWithValue("@sha", document.ContentSha256());
        await command.ExecuteNonQueryAsync(Ct);
    }

    private string Token(string audience, string subject, params Claim[] extra) =>
        fixture.Token("NV1", "Operator", subject, extra: [new Claim(PassportRegistration.AudienceClaim, audience), .. extra]);

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); }
        using var response = await fixture.Client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static string[] Fields(string body)
    {
        using var json = JsonDocument.Parse(body);
        return [.. json.RootElement.GetProperty("fields").EnumerateObject().Select(p => p.Name)];
    }

    private async Task<int> AuditCountAsync()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        using var command = new SqlCommand("SELECT COUNT(*) FROM passport.ReadAudit WHERE SerialNumber = @serial;", connection);
        command.Parameters.AddWithValue("@serial", Serial);
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }
}
