using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nvm.CommandStore;
using Nvm.EventStore;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Idempotency;
using Nvm.Quality.Hosting;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Handlers;
using Nvm.Traceability.Hosting;

namespace Nvm.IntegrationTests;

public sealed class TraceabilityCommandTests(SqlCommandStoreFixture fixture) : IClassFixture<SqlCommandStoreFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Serial = "NV1CL16238A54321";
    private static readonly DateTimeOffset At = new(2026, 8, 25, 3, 15, 42, TimeSpan.Zero);

    [Fact]
    public async Task CommandContext_LocksExecutionAndQuality_UntilCallerCommits()
    {
        await EventSchemaMigrator.UpgradeAsync(fixture.ConnectionString, Ct);
        await QualitySchemaMigrator.UpgradeAsync(fixture.ConnectionString, Ct);
        await TraceabilitySchemaMigrator.UpgradeAsync(fixture.ConnectionString, Ct);
        await fixture.ExecuteAsync("""
            INSERT INTO traceability.Routes (SiteId, ProductCode, RoutingVersion, StepsJson, TransitionsJson)
            VALUES ('NV1', 'LOCK-PRODUCT', 'r1', '[{"code":"STACK"}]',
                '[{"action":"StartStep","from":0,"to":1}]');
            """, _ => { }, Ct);
        const string serial = "NV1CL16238A54323";
        var born = new SerializeUnitCommand("NV1", "operator", "locked-birth", serial, At,
            "LOCK-PRODUCT", "WO-LOCK", "r1");
        (await RunAsync(born, (processor, command) => processor.SerializeAsync(command, Ct))).Accepted.ShouldBeTrue();
        var claim = SqlCommandStoreFixture.NewCommand("NV1", "operator", "context-lock", "context-lock");
        await fixture.SubmitAsync(claim, async session =>
        {
            var context = await new SqlUnitExecutionContextReader(session, Store(session), Facet(session)).ReadForCommandAsync(serial, Ct);
            context.ShouldNotBeNull();
            context.ExecutionState.ShouldBe("Scheduled");
            context.QualityState.ShouldBe("Pending");
            (await new SqlUnitExecutionContextReader(session, Store(session), Facet(session)).ReadForCommandAsync("DE1CL16238A54323", Ct)).ShouldBeNull();
            await using var competitor = new SqlConnection(fixture.ConnectionString);
            await competitor.OpenAsync(Ct);
            using var change = new SqlCommand("""
                UPDATE es.Streams WITH (NOWAIT) SET Version = Version
                WHERE SiteId = 'NV1' AND StreamId = 'NV1CL16238A54323';
                """, competitor);
            (await Should.ThrowAsync<SqlException>(() => change.ExecuteNonQueryAsync(Ct))).Number.ShouldBe(1222);
            // Facet chất lượng chưa có dòng (Pending): range lock vẫn chặn một hold chen vào giữa.
            change.CommandText = """
                INSERT INTO quality.UnitQuality WITH (NOWAIT)
                    (SiteId, SerialNumber, QualityState, ReasonCode, StreamVersion, LastEventId, UpdatedAt)
                VALUES ('NV1', 'NV1CL16238A54323', 'Held', 'TEST', 0, NEWID(), SYSDATETIMEOFFSET());
                """;
            (await Should.ThrowAsync<SqlException>(() => change.ExecuteNonQueryAsync(Ct))).Number.ShouldBe(1222);
            return new CollectionOutcome(true, "locked");
        }, cancellationToken: Ct);
        await fixture.ExecuteAsync("""
            UPDATE es.Streams WITH (NOWAIT) SET Version = Version
            WHERE SiteId = 'NV1' AND StreamId = 'NV1CL16238A54323';
            SELECT COUNT(*) FROM quality.UnitQuality WITH (NOWAIT)
            WHERE SiteId = 'NV1' AND SerialNumber = 'NV1CL16238A54323';
            """, _ => { }, Ct);
    }

    [Fact]
    public async Task DuplicateSerialization_CommitsIncidentHoldAndOutcomeTogether()
    {
        await EventSchemaMigrator.UpgradeAsync(fixture.ConnectionString, Ct);
        await QualitySchemaMigrator.UpgradeAsync(fixture.ConnectionString, Ct);
        await TraceabilitySchemaMigrator.UpgradeAsync(fixture.ConnectionString, Ct);
        await SeedRouteAsync();
        var first = new SerializeUnitCommand("NV1", "operator", "birth", Serial, At,
            "PRODUCT", "WO-1", "r1");
        var duplicate = new SerializeUnitCommand("NV1", "operator", "physical-second", Serial, At,
            "PRODUCT", "WO-1", "r1");

        var original = await RunAsync(first, (processor, command) => processor.SerializeAsync(command, Ct));
        original.Accepted.ShouldBeTrue();
        original.EventId.ShouldBe(first.IdempotencyKey.Value);
        var incident = await RunAsync(duplicate, (processor, command) => processor.SerializeAsync(command, Ct));
        incident.Accepted.ShouldBeTrue();
        incident.Quarantined.ShouldBeTrue();
        incident.ReasonCode.ShouldBe(UnitReasonCodes.DuplicateSerial);
        incident.EventId.ShouldBe(duplicate.IdempotencyKey.Value);

        var retry = await RunAsync(duplicate, (processor, command) => processor.SerializeAsync(command, Ct));
        retry.ShouldBe(incident);
        (await ReadStore().ReadStreamAsync("NV1", Serial, Ct))!.Version.ShouldBe(1);
        (await ReadStore().ReadStreamAsync("NV1", "duplicate:physical-second", Ct))!.Version.ShouldBe(1);
        (await ReadStore().ReadStreamAsync("DE1", Serial, Ct)).ShouldBeNull();
        (await fixture.ScalarAsync("""
            SELECT QualityState + ':' + ReasonCode FROM quality.UnitQuality
            WHERE SiteId = 'NV1' AND SerialNumber = 'NV1CL16238A54321';
            """, _ => { }, Ct)).ShouldBe("Held:DUPLICATE_SERIAL");
        // Facet phát đúng một UnitQuarantined trên stream của Quality, kể cả khi command được replay.
        (await ReadStore().ReadStreamAsync("NV1", "quality:" + Serial, Ct))!.Version.ShouldBe(1);
        (await fixture.ScalarAsync("""
            SELECT CONVERT(varchar(10), COUNT(*)) FROM traceability.DuplicateSerialIncidents
            WHERE SiteId = 'NV1' AND SerialNumber = 'NV1CL16238A54321';
            """, _ => { }, Ct)).ShouldBe("1");

        var start = new StartStepCommand("NV1", "operator", "start-after-hold", Serial, At,
            "STACK", "run-1", "station-1");
        var blocked = await RunAsync(start, (processor, command) => processor.StartAsync(command, Ct));
        blocked.Accepted.ShouldBeFalse();
        blocked.ReasonCode.ShouldBe(UnitReasonCodes.QualityHold);
    }

    [Fact]
    public async Task HttpCommands_RequireOperatorAndServerSite_ThenReturnAccepted()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TraceTestAuthHandler>(
            "Test", _ => { });
        builder.Services.AddNvmTraceabilityAuthorization();
        builder.Services.AddSingleton<ICommandDispatcher, TraceTestDispatcher>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapNvmTraceability();
        await app.StartAsync(Ct);
        using var client = app.GetTestClient();
        var command = new SerializeUnitCommand("NV1", "operator", "http-birth", Serial, At,
            "PRODUCT", "WO-1", "r1");
        var request = new TraceabilityRequest<SerializeUnitPayload>(
            command.IdempotencyKey.Value.ToString(), "NV1", At,
            new SerializeUnitPayload("http-birth", Serial, "PRODUCT", "WO-1", "r1"));
        const string route = "/api/v1/commands/traceability/serialize-unit";
        (await client.PostAsJsonAsync(route, request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("X-Test-Actor", "operator");
        client.DefaultRequestHeaders.Add("X-Test-Site", "NV1");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Viewer");
        (await client.PostAsJsonAsync(route, request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove("X-Test-Role");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Operator");
        (await client.PostAsJsonAsync(route, request with { SiteId = "DE1" }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync(route, request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task DemoRoute_SeedsIdempotently_AndRunsSerializedStepWithMeasurement()
    {
        await EventSchemaMigrator.UpgradeAsync(fixture.ConnectionString, Ct);
        await QualitySchemaMigrator.UpgradeAsync(fixture.ConnectionString, Ct);
        (await TraceabilityFixtureSeed.PrepareAsync(fixture.ConnectionString, Ct)).ShouldBe(4);
        (await TraceabilityFixtureSeed.PrepareAsync(fixture.ConnectionString, Ct)).ShouldBe(0);
        const string serial = "NV1CL16238A54322";
        var born = new SerializeUnitCommand("NV1", "operator", "demo-birth", serial, At,
            TraceabilityFixtureSeed.ProductCode, "WO-DEMO", TraceabilityFixtureSeed.RoutingVersion);
        var start = new StartStepCommand("NV1", "operator", "demo-start", serial, At,
            "STACK", "run-demo", "station-1");
        var measure = new RecordMeasurementCommand("NV1", "operator", "demo-measure", serial, At,
            "STACK", "run-demo", "station-1", "Voltage", 3.72m, "V");
        var finish = new CompleteStepCommand("NV1", "operator", "demo-finish", serial, At,
            "STACK", "run-demo");
        (await RunAsync(born, (processor, command) => processor.SerializeAsync(command, Ct))).StreamVersion
            .ShouldBe(1);
        (await RunAsync(start, (processor, command) => processor.StartAsync(command, Ct))).StreamVersion
            .ShouldBe(2);
        (await RunAsync(measure, (processor, command) => processor.RecordMeasurementAsync(command, Ct)))
            .StreamVersion.ShouldBe(3);
        (await RunAsync(finish, (processor, command) => processor.CompleteAsync(command, Ct))).StreamVersion
            .ShouldBe(4);
        var stream = (await ReadStore().ReadStreamAsync("NV1", serial, Ct))!;
        stream.Version.ShouldBe(4);
        foreach (var fact in stream.Events)
        {
            using var envelope = System.Text.Json.JsonDocument.Parse(fact.EnvelopeJson);
            envelope.RootElement.GetProperty("subject").GetString().ShouldBe($"urn:trace-unit:cell:{serial}");
            envelope.RootElement.GetProperty("correlationid").GetGuid().ShouldBe(fact.SourceEventId);
            envelope.RootElement.GetProperty("causationid").GetGuid().ShouldBe(fact.SourceEventId);
            envelope.RootElement.GetProperty("partitionkey").GetString().ShouldBe($"NV1:{serial}");
        }
        (await ReadStore().ReadStreamAsync("DE1", serial, Ct)).ShouldBeNull();
    }

    private async Task SeedRouteAsync()
    {
        await fixture.ExecuteAsync("""
            INSERT INTO traceability.Routes
                (SiteId, ProductCode, RoutingVersion, StepsJson, TransitionsJson)
            VALUES ('NV1', 'PRODUCT', 'r1',
                '[{"code":"STACK"}]',
                '[{"action":"StartStep","from":0,"to":1},{"action":"CompleteStep","from":1,"to":2}]');
            """, _ => { }, Ct);
    }

    private async Task<UnitCommandResult> RunAsync<TCommand>(TCommand command,
        Func<TraceabilityCommandProcessor, TCommand, Task<UnitCommandResult>> handle)
        where TCommand : UnitCommand
    {
        await using var session = new SqlCommandSession();
        var store = new SqlIdempotencyStore(session, fixture.Options(), fixture.SharedInMemory);
        var behavior = new IdempotencyBehavior<TCommand, UnitCommandResult>(store, TimeProvider.System);
        var events = Store(session);
        var facet = Facet(session);
        var adapters = new SqlTraceabilityAdapters(session, facet);
        var processor = new TraceabilityCommandProcessor(events, adapters, adapters, adapters,
            adapters, facet, TimeProvider.System);
        return await behavior.HandleAsync(command, () => handle(processor, command), Ct);
    }

    private SqlEventStore ReadStore() => Store(new SqlCommandSession());

    private SqlUnitQualityFacet Facet(SqlCommandSession session) => new(session, Store(session), TimeProvider.System);

    private SqlEventStore Store(SqlCommandSession session) =>
        new(session, new SqlEventStoreOptions { ConnectionString = fixture.ConnectionString },
            new EventUpcasterChain([]));
}

public sealed class TraceTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Actor", out var actor))
        { return Task.FromResult(AuthenticateResult.NoResult()); }
        var claims = new[]
        {
            new Claim("sub", actor.ToString()),
            new Claim("site_id", Request.Headers["X-Test-Site"].ToString()),
            new Claim(ClaimTypes.Role, Request.Headers["X-Test-Role"].ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
    }
}

public sealed class TraceTestDispatcher : ICommandDispatcher
{
    public Task<TResult> DispatchAsync<TResult>(ICommand<TResult> command,
        CancellationToken cancellationToken = default) =>
        Task.FromResult((TResult)(object)new UnitCommandResult(true, UnitReasonCodes.Accepted,
            command.IdempotencyKey.Value, 1));
}
