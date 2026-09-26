using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Nvm.App.Execution;
using Nvm.Kernel.Commands;
using Nvm.PublicObjectModel;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

// HTTP thật + JWT ký thật + process App thật. Chỉ issuer và database/broker thuộc fixture kiểm thử.
public sealed class ExecutionCommandHttpFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04").Build();
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.9-alpine").Build();
    private readonly string _password = "Aa1!" + Guid.NewGuid().ToString("N");
    private readonly RSA _rsa = RSA.Create(2048);
    private IContainer _rabbit = null!;
    private WebApplication _issuer = null!;
    private HttpClient _management = null!;
    private Process? _process;
    private Task<string>? _stdout;
    private Task<string>? _stderr;
    private string _authority = "";
    private int _appPort;
    public HttpClient Client { get; private set; } = null!;
    public string ConnectionString => _sql.GetConnectionString();
    public int ProcessId => _process!.Id;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _rabbit = new ContainerBuilder("rabbitmq:4.3.5-management")
            .WithEnvironment("RABBITMQ_DEFAULT_USER", "c06")
            .WithEnvironment("RABBITMQ_DEFAULT_PASS", _password)
            .WithPortBinding(5672, true).WithPortBinding(15672, true).Build();
        await Task.WhenAll(_sql.StartAsync(Ct), _postgres.StartAsync(Ct), _rabbit.StartAsync(Ct));
        await using (var connection = new SqlConnection(ConnectionString))
        {
            await connection.OpenAsync(Ct);
            using var command = connection.CreateCommand();
            command.CommandText = """
                DECLARE @ddl nvarchar(max) = N'CREATE LOGIN nvm_app WITH PASSWORD=' + QUOTENAME(@password, '''') + N';';
                EXEC(@ddl);
                CREATE USER nvm_app FOR LOGIN nvm_app;
                """;
            command.Parameters.AddWithValue("@password", _password);
            await command.ExecuteNonQueryAsync(Ct);
        }
        await CommandContextFixtureSeed.PrepareAsync(ConnectionString, Ct);
        await Nvm.EventStore.EventSchemaMigrator.UpgradeAsync(ConnectionString, Ct);
        await Nvm.Quality.Hosting.QualitySchemaMigrator.UpgradeAsync(ConnectionString, Ct);
        await Nvm.Traceability.Hosting.TraceabilitySchemaMigrator.UpgradeAsync(ConnectionString, Ct);
        PomSchemaMigrator.Upgrade(_postgres.GetConnectionString());

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _issuer = builder.Build();
        _issuer.Urls.Add("http://127.0.0.1:0");
        var key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa) { KeyId = "c06" });
        _issuer.MapGet("/.well-known/openid-configuration", () => new
        {
            issuer = _authority,
            jwks_uri = _authority + "/keys",
            id_token_signing_alg_values_supported = new[] { "RS256" },
        });
        _issuer.MapGet("/keys", () => new { keys = new[] { new { kty = "RSA", kid = "c06", use = "sig", alg = "RS256", n = key.N, e = key.E } } });
        await _issuer.StartAsync(Ct);
        _authority = _issuer.Urls.Single();
        _management = new HttpClient { BaseAddress = new Uri($"http://{_rabbit.Hostname}:{_rabbit.GetMappedPublicPort(15672)}/api/"), Timeout = TimeSpan.FromSeconds(5) };
        _management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("c06:" + _password)));
        await WaitUntilAsync(async () =>
        {
            using var response = await _management.GetAsync("overview", Ct);
            return response.IsSuccessStatusCode;
        }, "Broker test chưa sẵn sàng.");
        await PutAsync("exchanges/%2F/nvm.production-execution", new { type = "topic", durable = true, auto_delete = false, arguments = new { } });
        await PutAsync("queues/%2F/c06-evidence", new { durable = true, auto_delete = false, arguments = new Dictionary<string, object> { ["x-queue-type"] = "quorum" } });
        using var binding = await _management.PostAsJsonAsync("bindings/%2F/e/nvm.production-execution/q/c06-evidence", new { routing_key = "nvm.#", arguments = new { } }, Ct);
        binding.IsSuccessStatusCode.ShouldBeTrue();
        await StartAppAsync();
    }

    public string Token(string site = "NV1", string role = "Operator", string actor = "operator-test", bool expired = false, bool wrongAudience = false, string? secondSite = null)
    {
        var claims = new List<Claim> { new("sub", actor), new("site_id", site), new("mendix_roles", role) };
        if (secondSite is not null)
        { claims.Add(new("site_id", secondSite)); }
        var now = TimeProvider.System.GetUtcNow();
        var token = new JwtSecurityToken(_authority, wrongAudience ? "not-nvm-api" : "nvm-api", claims,
            now.AddHours(-2).UtcDateTime, (expired ? now.AddHours(-1) : now.AddHours(1)).UtcDateTime,
            new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "c06" }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<HttpResponseMessage> SendAsync(object body, string? token)
        => await SendToAsync(Client, body, token);

    public static async Task<HttpResponseMessage> SendToAsync(HttpClient client, object body, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/commands/production/record-data-collection") { Content = JsonContent.Create(body) };
        if (token is not null)
        { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); }
        return await client.SendAsync(request, Ct);
    }

    public static CollectionRequest Request(string site = "NV1", string? serial = null)
    {
        var unit = OperatorFixture.GenerateUnits().First(u => u.SiteId == site && (serial is null
            ? u.StepCode == "EOL" && u.ExecutionState == "Running" && u.QualityState == "Pending"
            : u.SerialNumber == serial));
        var submission = Guid.NewGuid().ToString("D");
        return new(IdempotencyKey.FromNaturalKey(site, "RecordDataCollection", submission).Value, site,
            DateTimeOffset.Parse("2026-09-15T03:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
            new(submission, unit.SerialNumber, unit.OperationRunId, unit.StepCode, unit.EquipmentPath, "PackVoltage", 401.250m, "V"));
    }

    public async Task RestartAppAsync()
    {
        await StopAppAsync();
        await StartAppAsync();
    }

    public async Task<PeerApp> StartPeerAsync()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var peerPort = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in _process!.StartInfo.ArgumentList)
        { info.ArgumentList.Add(argument); }
        foreach (var variable in _process.StartInfo.Environment)
        { info.Environment[variable.Key] = variable.Value; }
        info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{peerPort}";
        var peer = new PeerApp(Process.Start(info)!, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{peerPort}"), Timeout = TimeSpan.FromSeconds(45) });
        try
        {
            await WaitUntilAsync(async () =>
            {
                using var response = await peer.Client.GetAsync("/health/ready", Ct);
                return response.IsSuccessStatusCode;
            }, "Peer app test chưa ready.");
            return peer;
        }
        catch { await peer.DisposeAsync(); throw; }
    }

    public async Task StartAppAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var assembly = Environment.GetEnvironmentVariable("NVM_C06_TEST_APP")
            ?? Path.Combine(directory.Parent!.Parent!.FullName, "Nvm.App.Execution", directory.Name, "Nvm.App.Execution.dll");
        var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        _appPort = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(assembly);
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{_appPort}";
        info.Environment["Logging__LogLevel__Default"] = "Warning";
        info.Environment["NVM_COMMANDS__ConnectionString"] = new SqlConnectionStringBuilder(ConnectionString) { UserID = "nvm_app", Password = _password }.ConnectionString;
        info.Environment["NVM_POM__ConnectionString"] = _postgres.GetConnectionString();
        info.Environment["NVM_POM__Authority"] = _authority;
        info.Environment["NVM_POM__MetadataAddress"] = _authority + "/.well-known/openid-configuration";
        info.Environment["NVM_POM__Audience"] = "nvm-api";
        info.Environment["NVM_RABBITMQ_HOST"] = _rabbit.Hostname;
        info.Environment["NVM_PORT_RABBITMQ"] = _rabbit.GetMappedPublicPort(5672).ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["NVM_RABBITMQ_USER"] = "c06";
        info.Environment["NVM_RABBITMQ_PASSWORD"] = _password;
        _process = Process.Start(info)!;
        _stdout = _process.StandardOutput.ReadToEndAsync(Ct);
        _stderr = _process.StandardError.ReadToEndAsync(Ct);
        Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_appPort}"), Timeout = TimeSpan.FromSeconds(45) };
        await WaitUntilAsync(async () =>
        {
            if (_process.HasExited)
            { throw new InvalidOperationException($"App test exited with code {_process.ExitCode}; output withheld to protect configuration."); }
            using var response = await Client.GetAsync("/health/ready", Ct);
            return response.IsSuccessStatusCode;
        }, "App test chưa ready.");
    }

    public async Task<JsonElement[]> DrainEventsAsync()
    {
        using var response = await _management.PostAsJsonAsync("queues/%2F/c06-evidence/get", new { count = 100, ackmode = "ack_requeue_false", encoding = "auto", truncate = 100000 }, Ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    public async Task StopBrokerAsync() => await _rabbit.StopAsync(Ct);

    public async Task<int> OutcomeCountAsync(CollectionRequest request)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM command_store.CommandOutcomes WHERE SiteId=@site AND IdempotencyKey=@key";
        query.Parameters.AddWithValue("@site", request.SiteId);
        query.Parameters.AddWithValue("@key", request.IdempotencyKey);
        return (int)(await query.ExecuteScalarAsync(Ct))!;
    }

    public async Task<int[]> EventIntentCountsAsync(CollectionRequest request)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT (SELECT COUNT(*) FROM es.Events WHERE SiteId=@site AND SourceEventId=@key),
                   (SELECT COUNT(*) FROM es.Outbox WHERE SiteId=@site AND EventId=@key);
            """;
        query.Parameters.AddWithValue("@site", request.SiteId);
        query.Parameters.AddWithValue("@key", request.IdempotencyKey);
        using var reader = await query.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        return [reader.GetInt32(0), reader.GetInt32(1)];
    }

    public async Task<JsonElement> StoredEnvelopeAsync(CollectionRequest request)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT CloudEventJson FROM es.Events WHERE SiteId=@site AND SourceEventId=@key";
        query.Parameters.AddWithValue("@site", request.SiteId);
        query.Parameters.AddWithValue("@key", request.IdempotencyKey);
        using var json = JsonDocument.Parse((string)(await query.ExecuteScalarAsync(Ct))!);
        return json.RootElement.Clone();
    }

    public async Task<JsonElement?> StoredRecordAsync(CollectionRequest request)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT (SELECT * FROM execution.DataCollection WHERE SiteId=@site AND IdempotencyKey=@key FOR JSON PATH)";
        query.Parameters.AddWithValue("@site", request.SiteId);
        query.Parameters.AddWithValue("@key", request.IdempotencyKey);
        if (await query.ExecuteScalarAsync(Ct) is not string raw)
        { return null; }
        using var json = JsonDocument.Parse(raw);
        var rows = json.RootElement.EnumerateArray().ToArray();
        rows.Length.ShouldBeLessThanOrEqualTo(1);
        return rows.Length == 0 ? null : rows[0].Clone();
    }

    public async Task<string> ContextJsonAsync(CollectionRequest request, string? replacement = null)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT ContextJson FROM execution.UnitContext WHERE SiteId=@site AND SerialNumber=@serial";
        if (replacement is not null)
        { query.CommandText = "UPDATE execution.UnitContext SET ContextJson=@json WHERE SiteId=@site AND SerialNumber=@serial; SELECT ContextJson FROM execution.UnitContext WHERE SiteId=@site AND SerialNumber=@serial"; }
        query.Parameters.AddWithValue("@site", request.SiteId);
        query.Parameters.AddWithValue("@serial", request.Payload.Serial);
        if (replacement is not null)
        { query.Parameters.AddWithValue("@json", replacement); }
        return (string)(await query.ExecuteScalarAsync(Ct))!;
    }

    public async Task FailOutcomeUpdateAsync(bool enabled)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        using var query = connection.CreateCommand();
        query.CommandText = "DROP TRIGGER IF EXISTS command_store.C06FailOutcome;";
        if (enabled)
        { query.CommandText = "CREATE TRIGGER command_store.C06FailOutcome ON command_store.CommandOutcomes AFTER UPDATE AS BEGIN THROW 51000, 'C06 injected outcome failure', 1; END;"; }
        await query.ExecuteNonQueryAsync(Ct);
    }

    public async Task<int[]> CollectionPermissionsAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        using var query = connection.CreateCommand();
        query.CommandText = """
            EXECUTE AS USER = 'nvm_app';
            SELECT HAS_PERMS_BY_NAME('execution.DataCollection','OBJECT','SELECT'),
                HAS_PERMS_BY_NAME('execution.DataCollection','OBJECT','INSERT'),
                HAS_PERMS_BY_NAME('execution.DataCollection','OBJECT','UPDATE'),
                HAS_PERMS_BY_NAME('execution.DataCollection','OBJECT','DELETE');
            REVERT;
            """;
        using var reader = await query.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return [reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)];
    }

    public async Task<JsonElement[]> EventsForAsync(CollectionRequest request, int timeoutSeconds = 5)
    {
        var result = new List<JsonElement>();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var rows = await DrainEventsAsync();
            result.AddRange(rows.Where(row => row.GetProperty("properties").GetProperty("headers")
                .GetProperty("ce_id").GetString() == request.IdempotencyKey.ToString("D")));
            if (result.Count > 0)
            { break; }
            await Task.Delay(100, Ct);
        }
        return result.ToArray();
    }

    public async Task<string> DeliveryStateAsync(CollectionRequest request)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        using var command = new SqlCommand("""
            SELECT Attempt, ClaimId, NextAttemptAt, DispatchedAt FROM es.Outbox
            WHERE SiteId = @site AND EventId = @event;
            """, connection);
        command.Parameters.AddWithValue("site", request.SiteId);
        command.Parameters.AddWithValue("event", request.IdempotencyKey);
        using var reader = await command.ExecuteReaderAsync(Ct);
        if (!await reader.ReadAsync(Ct))
        { return "outbox absent"; }
        var retry = reader.GetDateTimeOffset(2) - TimeProvider.System.GetUtcNow();
        var claimed = !await reader.IsDBNullAsync(1, Ct);
        var dispatched = !await reader.IsDBNullAsync(3, Ct);
        return $"attempt={reader.GetInt32(0)}, claimed={claimed}, dispatched={dispatched}, dueIn={retry.TotalSeconds:F1}s";
    }
    public async Task StartBrokerAsync()
    {
        await _rabbit.StartAsync(Ct);
        // Docker có thể cấp lại host port động khi start container đã stop.
        _management.Dispose();
        _management = new HttpClient { BaseAddress = new Uri($"http://{_rabbit.Hostname}:{_rabbit.GetMappedPublicPort(15672)}/api/"), Timeout = TimeSpan.FromSeconds(5) };
        _management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("c06:" + _password)));
        await WaitUntilAsync(async () =>
        {
            using var response = await _management.GetAsync("overview", Ct);
            return response.IsSuccessStatusCode;
        }, "Broker chưa ready sau restart.");
    }

    private async Task PutAsync(string path, object body)
    {
        using var response = await _management.PutAsJsonAsync(path, body, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"Management {path}: {(int)response.StatusCode}; {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> probe, string message)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            try
            { if (await probe()) { return; } }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(200, Ct);
        }
        throw new TimeoutException(message);
    }

    public async Task StopAppAsync()
    {
        if (_process is not null)
        {
            if (!_process.HasExited)
            { _process.Kill(entireProcessTree: true); }
            await _process.WaitForExitAsync(Ct);
            _process.Dispose();
            _process = null;
            if (_stdout is not null)
            { _ = await _stdout; }
            if (_stderr is not null)
            { _ = await _stderr; }
        }
        Client?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAppAsync();
        if (_issuer is not null)
        { await _issuer.DisposeAsync(); }
        _management?.Dispose();
        _rsa.Dispose();
        if (_rabbit is not null)
        { await _rabbit.DisposeAsync(); }
        await _postgres.DisposeAsync();
        await _sql.DisposeAsync();
    }
}

public sealed class PeerApp(Process process, HttpClient client) : IAsyncDisposable
{
    private readonly Task<string> _stdout = process.StandardOutput.ReadToEndAsync();
    private readonly Task<string> _stderr = process.StandardError.ReadToEndAsync();
    public HttpClient Client => client;
    public int ProcessId => process.Id;
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        { process.Kill(entireProcessTree: true); }
        await process.WaitForExitAsync();
        _ = await _stdout;
        _ = await _stderr;
        process.Dispose();
        client.Dispose();
    }
}

public sealed record CollectionRequest(Guid IdempotencyKey, string SiteId, DateTimeOffset OccurredAt, CollectionPayload Payload);
public sealed record CollectionPayload(string SubmissionId, string Serial, string OperationRunId, string StepCode, string EquipmentPath, string SignalCode, decimal Value, string UnitOfMeasure);
