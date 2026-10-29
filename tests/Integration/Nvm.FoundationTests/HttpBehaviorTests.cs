using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nvm.Foundation;
using Nvm.Foundation.Web;
using Nvm.FoundationTests.LabModel;

namespace Nvm.FoundationTests;

/// <summary>Service layer HTTP: OData theo app, POST command, token có hạn, reading function (R2).</summary>
public sealed class HttpBehaviorTests
{
    private const string Issuer = "nvm-test";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("nvm-test-signing-key-only-for-tests-32b"));

    [Fact]
    public async Task Entity_set_applies_filter_order_top_and_count()
    {
        var (h, client) = await StartAsync();
        foreach (var (nid, uom) in new[] { ("MAT-1", "pcs"), ("MAT-2", "kg"), ("MAT-3", "pcs") })
        {
            h.Run(new CreateLabMaterial { NId = nid, UnitOfMeasure = uom });
        }

        var body = await GetJsonAsync(client, "LabApp/odata/LabMaterial?$filter=UnitOfMeasure eq 'pcs'&$orderby=NId desc&$top=1&$count=true");

        body["@odata.count"]!.GetValue<long>().ShouldBe(2);
        body["value"]!.AsArray().Select(v => v!["NId"]!.GetValue<string>()).ShouldBe(["MAT-3"]);
    }

    [Fact]
    public async Task Entity_not_imported_by_the_app_cannot_be_read_there()
    {
        var (_, client) = await StartAsync();

        (await client.GetAsync(Url("LabApp/odata/LabOrderNote"), Cancel)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(Url("OtherApp/odata/LabOrderNote"), Cancel)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Single_entity_is_read_by_its_id()
    {
        var (h, client) = await StartAsync();
        var id = h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" }).As<CreatedResponse>().Id;

        var body = await GetJsonAsync(client, $"LabApp/odata/LabMaterial({id})");

        body["NId"]!.GetValue<string>().ShouldBe("MAT-1");
        (await client.GetAsync(Url($"LabApp/odata/LabMaterial({Guid.NewGuid()})"), Cancel)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Command_is_posted_by_its_published_name()
    {
        var (h, client) = await StartAsync();

        var ok = await PostAsync(client, "LabApp/odata/CreateLabMaterial", """{"NId":"MAT-9","UnitOfMeasure":"kg"}""");
        var missing = await PostAsync(client, "LabApp/odata/CreateLabMaterial", """{"UnitOfMeasure":"kg"}""");
        var extension = await PostAsync(client, "LabApp/odata/LabExt_TagLabOrder", """{"OrderNId":"ORD-1"}""");
        var notThere = await PostAsync(client, "LabExt/odata/TagLabOrder", """{"OrderNId":"ORD-1"}""");

        ok.Status.ShouldBe(HttpStatusCode.OK);
        ok.Body["Succeeded"]!.GetValue<bool>().ShouldBeTrue();
        missing.Status.ShouldBe(HttpStatusCode.BadRequest);
        missing.Body["error"]!["code"]!.GetValue<string>().ShouldBe("6400");
        extension.Status.ShouldBe(HttpStatusCode.OK);
        notThere.Status.ShouldBe(HttpStatusCode.NotFound);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabMaterial>()}] WHERE NId = 'MAT-9'").ShouldBe(1);
    }

    [Fact]
    public async Task Expired_token_is_refused_with_6401_lab_L05()
    {
        var (_, client) = await StartAsync(Token(expires: Now.AddSeconds(-1)));

        var response = await client.GetAsync(Url("LabApp/odata/LabMaterial"), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel))!;
        body["error"]!["code"]!.GetValue<string>().ShouldBe(MomAuthentication.ExpiredTokenCode);
    }

    [Fact]
    public async Task Call_without_a_token_is_refused()
    {
        var (_, client) = await StartAsync(token: null);

        (await client.GetAsync(Url("LabApp/odata/LabMaterial"), Cancel)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Another_plant_in_the_url_is_not_served()
    {
        var (_, client) = await StartAsync();

        (await client.GetAsync(new Uri("/mom/XX9/Application/LabApp/odata/LabMaterial", UriKind.Relative), Cancel))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Roles_in_the_token_reach_the_state_machine()
    {
        var (h, client) = await StartAsync(Token(Now.AddMinutes(10), "operator"));
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });
        h.Run(new CreateLabOrder { NId = "ORD-1", MaterialNId = "MAT-1" });
        foreach (var status in new[] { "New", "Active" })
        {
            h.Run(new DefineStatus { Machine = "LabOrder", Status = status }, "Foundation");
        }

        h.Run(new DefineTransition { Machine = "LabOrder", ToStatus = "New" }, "Foundation");
        h.Run(new DefineTransition { Machine = "LabOrder", FromStatus = "New", ToStatus = "Active" }, "Foundation");
        h.Run(new AssignTransitionRole { Machine = "LabOrder", FromStatus = "New", ToStatus = "Active", Role = "shift-lead" }, "Foundation");

        (await PostAsync(client, "LabApp/odata/SetLabOrderStatus", """{"OrderNId":"ORD-1","Status":"New"}""")).Status.ShouldBe(HttpStatusCode.OK);
        var refused = await PostAsync(client, "LabApp/odata/SetLabOrderStatus", """{"OrderNId":"ORD-1","Status":"Active"}""");
        refused.Body["Error"]!["ErrorCode"]!.GetValue<int>().ShouldBe(7102);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(Now.AddMinutes(10), "shift-lead"));
        (await PostAsync(client, "LabApp/odata/SetLabOrderStatus", """{"OrderNId":"ORD-1","Status":"Active"}""")).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reading_function_returns_a_deferred_query_that_odata_keeps_filtering()
    {
        var (h, client) = await StartAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });
        foreach (var (nid, qty) in new[] { ("ORD-1", 2m), ("ORD-2", 8m), ("ORD-3", 9m) })
        {
            h.Run(new CreateLabOrder { NId = nid, MaterialNId = "MAT-1", Quantity = qty });
        }

        // View là phần cài tay ngoài gói: Update Database không tạo nó.
        h.Execute($"CREATE VIEW dbo.LabOrderSummary AS SELECT NId AS OrderNId, MaterialNId, Quantity FROM dbo.[{h.TableOf<LabOrder>()}] WHERE IsDeleted = 0;");

        var body = await GetJsonAsync(client, "LabApp/odata/OrdersOfMaterial(MaterialNId='MAT-1')?$filter=Quantity gt 5&$orderby=OrderNId");

        body["value"]!.AsArray().Select(v => v!["OrderNId"]!.GetValue<string>()).ShouldBe(["ORD-2", "ORD-3"]);
    }

    [Fact]
    public async Task Missing_view_gives_http_200_with_a_cut_body_lab_L15()
    {
        var (h, client) = await StartAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        var response = await client.GetAsync(Url("LabApp/odata/OrdersOfMaterial(MaterialNId='MAT-1')"),
            HttpCompletionOption.ResponseHeadersRead, Cancel);

        // Mã 200 đã gửi trước khi query chạy; lỗi "view không tồn tại" chỉ còn cắt được thân.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(Cancel);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return;
        }

        Should.Throw<JsonException>(() => JsonDocument.Parse(text));
    }

    [Fact]
    public async Task Reading_function_that_throws_when_called_is_a_500_carrying_its_message()
    {
        var (_, client) = await StartAsync();

        var response = await client.GetAsync(Url("LabApp/odata/FailingOrders(MaterialNId='MAT-1')"), Cancel);

        // Lỗi ném trước khi có dòng nào được ghi: chưa gửi 200, nên còn trả được mã lỗi và câu lỗi (khác lab L15).
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel))!;
        body["error"]!["code"]!.GetValue<string>().ShouldBe("ReadingFunctionFailed");
        body["error"]!["message"]!.GetValue<string>().ShouldBe("Configuration key Lab.Connection is missing.");
    }

    [Fact]
    public async Task Invalid_filter_is_a_400()
    {
        var (_, client) = await StartAsync();

        var response = await client.GetAsync(Url("LabApp/odata/LabMaterial?$filter=Nope eq 'x'"), Cancel);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Thư viện JWT kiểm hạn token theo đồng hồ thật, không theo FakeTimeProvider của harness.
    private static DateTimeOffset Now => TimeProvider.System.GetUtcNow();

    private static Uri Url(string path) => new($"/mom/NV1/Application/{path}", UriKind.Relative);

    private static async Task<(FoundationHarness Harness, HttpClient Client)> StartAsync(string? token = "default")
    {
        var h = await FoundationHarness.CreateAsync();
        var options = new MomWebOptions { Plant = "NV1", Issuer = Issuer, SigningKey = Key };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMomAuthentication(options);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMom(h.Executor, options);
        await app.StartAsync(Cancel);
        var client = app.GetTestClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                token == "default" ? Token(Now.AddMinutes(10)) : token);
        }

        return (h, client);
    }

    private static string Token(DateTimeOffset expires, params string[] roles) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Subject = new ClaimsIdentity([new Claim("preferred_username", "tester")]),
            Claims = new Dictionary<string, object> { ["realm_access"] = new Dictionary<string, object> { ["roles"] = roles } },
            NotBefore = expires.AddMinutes(-10).UtcDateTime,
            IssuedAt = expires.AddMinutes(-10).UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(Url(path), Cancel);
        var text = await response.Content.ReadAsStringAsync(Cancel);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!;
    }

    private static async Task<(HttpStatusCode Status, JsonNode Body)> PostAsync(HttpClient client, string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(Url(path), content, Cancel);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancel))!);
    }
}
