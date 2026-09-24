using System.Collections.Immutable;
using System.Text.Json;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Traceability;
using Nvm.EventStore;
using Nvm.Kernel.EventSourcing;
using Nvm.Projections;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

public sealed class ProjectionEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RuntimeRole_WorkerRecoversPendingRowsAfterRestart_AndCannotAlterSourceFacts()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ExecuteAsync("CREATE ROLE nvm_projection NOLOGIN;");
        await ProjectionSchemaMigrator.UpgradeAsync(fixture.DataSource, Ct);
        await using var runtime = fixture.CreateRuntimeDataSource();
        var health = new ProjectionStoreHealthCheck(runtime);
        var ready = await health.CheckHealthAsync(new(), Ct);
        ready.Status.ShouldBe(HealthStatus.Healthy, ready.Description);
        var inbox = new ProductionUnitProjectionInbox(runtime);
        var serial = "NV1CL16238A00123";
        await inbox.EnqueueAsync(Fact(2, "NV1", serial, 2, Start("NV1", serial)), Ct);
        await inbox.EnqueueAsync(Fact(1, "NV1", serial, 1, Born("NV1", serial)), Ct);
        using (var worker = Worker(inbox))
        {
            await worker.StartAsync(Ct);
            await WaitForVersionAsync(2);
            await worker.StopAsync(Ct);
        }
        await inbox.EnqueueAsync(Fact(3, "NV1", serial, 3, Measure("NV1", serial)), Ct);
        using (var restarted = Worker(new ProductionUnitProjectionInbox(runtime)))
        {
            await restarted.StartAsync(Ct);
            await WaitForVersionAsync(3);
            await restarted.StopAsync(Ct);
        }
        await using (var tamper = runtime.CreateCommand("UPDATE rm.unit_projection_inbox SET fact = '{}'::jsonb;"))
        {
            var error = await Should.ThrowAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync(Ct));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        await using (var erase = runtime.CreateCommand("DELETE FROM rm.unit_projection_inbox;"))
        {
            var error = await Should.ThrowAsync<PostgresException>(() => erase.ExecuteNonQueryAsync(Ct));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        await fixture.ExecuteAsync("REVOKE INSERT ON rm.unit_projection_inbox FROM nvm_projection;");
        (await health.CheckHealthAsync(new(), Ct)).Status.ShouldBe(HealthStatus.Unhealthy);

        static ProductionUnitProjectionWorker Worker(ProductionUnitProjectionInbox queue) =>
            new(queue, ["NV1", "DE1"], TimeProvider.System, NullLogger<ProductionUnitProjectionWorker>.Instance);

        async Task WaitForVersionAsync(long version)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (await fixture.CheckpointAsync("NV1") != version)
            { await Task.Delay(25, deadline.Token); }
            (await fixture.CountAsync("NV1")).ShouldBe(1);
            (await fixture.CountAsync("DE1")).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Inbox_OutOfOrderDeliveryAndConcurrentWorkers_ApplyEveryVersionOnce()
    {
        await using var fixture = await Fixture.StartAsync();
        var inbox = new ProductionUnitProjectionInbox(fixture.DataSource);
        var serial = "NV1CL16238A00123";
        var born = Fact(10, "NV1", serial, 1, Born("NV1", serial));
        var started = Fact(30, "NV1", serial, 2, Start("NV1", serial));
        var measured = Fact(40, "NV1", serial, 3, Measure("NV1", serial));
        await inbox.EnqueueAsync(measured, Ct);
        (await inbox.DispatchAsync("NV1", cancellationToken: Ct)).ShouldBe(0);
        await inbox.EnqueueAsync(started, Ct);
        (await inbox.DispatchAsync("NV1", cancellationToken: Ct)).ShouldBe(0);
        await Task.WhenAll(inbox.EnqueueAsync(born, Ct), inbox.EnqueueAsync(born, Ct));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => inbox.DispatchAsync("NV1", cancellationToken: Ct)));
        attempts.Sum().ShouldBe(3);
        (await fixture.CountAsync("NV1")).ShouldBe(1);
        (await fixture.JsonAsync("NV1")).ShouldContain("\"stream_version\": 3");
        (await fixture.CountAsync("DE1")).ShouldBe(0);
        await inbox.EnqueueAsync(born, Ct);
        (await inbox.DispatchAsync("NV1", cancellationToken: Ct)).ShouldBe(0);
        await Should.ThrowAsync<InvalidDataException>(() =>
            inbox.EnqueueAsync(born with { PayloadJson = "{}" }, Ct));
    }

    [Fact]
    public async Task Inbox_FailureBetweenProjectionAndAcknowledgement_RollsBackBoth()
    {
        await using var fixture = await Fixture.StartAsync();
        var inbox = new ProductionUnitProjectionInbox(fixture.DataSource);
        var serial = "NV1CL16238A00123";
        await inbox.EnqueueAsync(Fact(1, "NV1", serial, 1, Born("NV1", serial)), Ct);
        await fixture.ExecuteAsync("""
            CREATE FUNCTION rm.fail_inbox_ack() RETURNS trigger LANGUAGE plpgsql AS '
            BEGIN RAISE EXCEPTION ''injected ack failure''; END';
            CREATE TRIGGER fail_inbox_ack BEFORE UPDATE ON rm.unit_projection_inbox
                FOR EACH ROW EXECUTE FUNCTION rm.fail_inbox_ack();
            """);
        await Should.ThrowAsync<PostgresException>(() => inbox.DispatchAsync("NV1", cancellationToken: Ct));
        (await fixture.CountAsync("NV1")).ShouldBe(0);
        (await fixture.CheckpointAsync("NV1")).ShouldBe(0);
        await fixture.ExecuteAsync("DROP TRIGGER fail_inbox_ack ON rm.unit_projection_inbox;");
        (await inbox.DispatchAsync("NV1", cancellationToken: Ct)).ShouldBe(1);
        (await inbox.DispatchAsync("NV1", cancellationToken: Ct)).ShouldBe(0);
        (await fixture.CountAsync("NV1")).ShouldBe(1);
    }

    [Fact]
    public async Task SqlServerFeed_ProjectsCommittedEventIntoPostgres()
    {
        await using var sql = new SqlCommandStoreFixture();
        await sql.InitializeAsync();
        await EventSchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await using var postgres = await Fixture.StartAsync();
        var serial = "NV1CL16238A00123";
        var fact = Fact(1, "NV1", serial, 1, Born("NV1", serial));
        var entry = new NewStreamEvent(fact.SourceEventId, fact.EventType, fact.SchemaVersion,
            fact.PayloadJson, "{}", fact.OccurredAt, fact.RecordedAt);
        var command = SqlCommandStoreFixture.NewCommand("NV1", "operator", Guid.NewGuid().ToString("N"),
            "project committed event");
        await sql.SubmitAsync(command, async session =>
        {
            var store = new SqlEventStore(session,
                new SqlEventStoreOptions { ConnectionString = sql.ConnectionString },
                new EventUpcasterChain([]));
            await store.AppendAsync("NV1", serial, "production-unit", 0,
                ImmutableArray.Create(entry), Ct);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);

        var source = new SqlGlobalEventFeed(sql.ConnectionString);
        var read = await source.ReadAsync("NV1", 0, 100, Ct);
        read.ShouldContain(e => e.SourceEventId == fact.SourceEventId);
        (await source.ReadAsync("DE1", 0, 100, Ct)).ShouldNotContain(e => e.SourceEventId == fact.SourceEventId);
        (await source.FindAsync("DE1", fact.SourceEventId, Ct)).ShouldBeNull();
        (await source.FindAsync("NV1", fact.SourceEventId, Ct))!.StreamId.ShouldBe(serial);

        var inbox = new ProductionUnitProjectionInbox(postgres.DataSource);
        await using var provider = new ServiceCollection()
            .AddSingleton(source).AddSingleton(inbox)
            .AddMassTransitTestHarness(bus => bus.AddConsumer<ProductionUnitProjectionConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(JsonSerializer.Deserialize<ProductionUnitSerialized>(fact.PayloadJson, Json)!, Ct);
            (await harness.GetConsumerHarness<ProductionUnitProjectionConsumer>()
                .Consumed.Any<ProductionUnitSerialized>(Ct)).ShouldBeTrue();
            (await inbox.DispatchAsync("NV1", cancellationToken: Ct)).ShouldBe(1);
        }
        finally { await harness.Stop(cancellationToken: TestContext.Current.CancellationToken); }

        var projection = new ProductionUnitProjection(postgres.DataSource, source);
        await projection.CatchUpAsync("NV1", 100, Ct);
        (await postgres.JsonAsync("NV1")).ShouldContain(serial);
    }

    [Fact]
    public async Task ReplayAndRebuild_PreserveRowsAndSiteBoundary_IncludingLateLowerSequence()
    {
        await using var fixture = await Fixture.StartAsync();
        var feed = new Feed();
        var a = "NV1CL16238A00123";
        var b = "NV1CL16238A00124";
        var other = "DE1CL16238A00123";
        feed.Events.Add(Fact(20, "NV1", a, 1, Born("NV1", a)));
        feed.Events.Add(Fact(30, "NV1", a, 2, Start("NV1", a)));
        feed.Events.Add(Fact(40, "NV1", a, 3, Measure("NV1", a)));
        feed.Events.Add(Fact(50, "NV1", a, 4, Complete("NV1", a)));
        feed.Events.Add(Fact(25, "DE1", other, 1, Born("DE1", other)));
        var projection = new ProductionUnitProjection(fixture.DataSource, feed);

        (await projection.CatchUpAsync("NV1", 2, Ct)).ShouldBe(50);
        (await fixture.JsonAsync("NV1")).ShouldContain("Completed");
        (await fixture.JsonAsync("DE1")).ShouldBe("[]");

        // A transaction on another stream may commit after a higher SQL ID was seen.
        feed.Events.Add(Fact(10, "NV1", b, 1, Born("NV1", b)));
        await projection.CatchUpAsync("NV1", 2, Ct);
        (await fixture.CountAsync("NV1")).ShouldBe(2);
        (await fixture.CheckpointAsync("NV1")).ShouldBe(50);
        var before = await fixture.JsonAsync("NV1");

        await projection.RebuildAsync("NV1", Ct);
        (await fixture.CountAsync("NV1")).ShouldBe(0);
        (await fixture.CheckpointAsync("NV1")).ShouldBe(0);
        await projection.CatchUpAsync("NV1", 2, Ct);
        (await fixture.JsonAsync("NV1")).ShouldBe(before);
        await projection.CatchUpAsync("DE1", 2, Ct);
        (await fixture.CountAsync("DE1")).ShouldBe(1);
        (await fixture.JsonAsync("NV1")).ShouldBe(before);
    }

    [Fact]
    public async Task FailedEvent_RollsBackReadModelAndCheckpoint_ThenRetryAppliesExactlyOnce()
    {
        await using var fixture = await Fixture.StartAsync();
        var serial = "NV1CL16238A00123";
        var feed = new Feed();
        feed.Events.Add(Fact(1, "NV1", serial, 1, Born("NV1", serial)));
        feed.Events.Add(Fact(2, "NV1", serial, 2, Start("NV1", serial)));
        feed.Events.Add(Fact(3, "NV1", serial, 3, Measure("NV1", serial)));
        feed.Events.Add(Fact(4, "NV1", serial, 4, Complete("NV1", serial)));
        var projection = new ProductionUnitProjection(fixture.DataSource, feed);
        await fixture.ExecuteAsync("""
            CREATE FUNCTION rm.fail_version_three() RETURNS trigger LANGUAGE plpgsql AS '
            BEGIN IF NEW.stream_version = 3 THEN RAISE EXCEPTION ''injected failure''; END IF;
                  RETURN NEW; END';
            CREATE TRIGGER fail_version_three BEFORE UPDATE ON rm.unit_current
                FOR EACH ROW EXECUTE FUNCTION rm.fail_version_three();
            """);

        await Should.ThrowAsync<PostgresException>(() => projection.CatchUpAsync("NV1", 4, Ct));
        (await fixture.CountAsync("NV1")).ShouldBe(0);
        (await fixture.CheckpointAsync("NV1")).ShouldBe(0);

        await fixture.ExecuteAsync("DROP TRIGGER fail_version_three ON rm.unit_current;");
        await projection.CatchUpAsync("NV1", 4, Ct);
        var after = await fixture.JsonAsync("NV1");
        (await fixture.CountAsync("NV1")).ShouldBe(1);
        (await fixture.CheckpointAsync("NV1")).ShouldBe(4);
        await projection.CatchUpAsync("NV1", 4, Ct);
        (await fixture.JsonAsync("NV1")).ShouldBe(after);
        (await fixture.CheckpointAsync("NV1")).ShouldBe(4);
    }

    [Fact]
    public async Task GapOrCrossSitePayload_FailsWithoutAdvancingCheckpoint()
    {
        await using var fixture = await Fixture.StartAsync();
        var serial = "NV1CL16238A00123";
        var feed = new Feed();
        feed.Events.Add(Fact(1, "NV1", serial, 1, Born("NV1", serial)));
        feed.Events.Add(Fact(2, "NV1", serial, 3, Measure("NV1", serial)));
        var projection = new ProductionUnitProjection(fixture.DataSource, feed);
        await Should.ThrowAsync<InvalidDataException>(() => projection.CatchUpAsync("NV1", 2, Ct));
        (await fixture.CountAsync("NV1")).ShouldBe(0);
        (await fixture.CheckpointAsync("NV1")).ShouldBe(0);

        feed.Events[1] = Fact(2, "NV1", serial, 2, Born("DE1", "DE1CL16238A00123"));
        await Should.ThrowAsync<InvalidDataException>(() => projection.CatchUpAsync("NV1", 2, Ct));
        (await fixture.CountAsync("NV1")).ShouldBe(0);
    }

    private static StoredStreamEvent Fact(long sequence, string site, string stream, long version, IDomainEvent value)
    {
        var type = EventTypeName.Of(value.GetType());
        return new StoredStreamEvent(sequence, site, stream, version, value.EventId, type.Value,
            type.Version, JsonSerializer.Serialize(value, value.GetType(), Json), "{}", Now, Now);
    }

    private static ProductionUnitSerialized Born(string site, string serial) =>
        new(Guid.NewGuid(), Now, Now, site, serial, "Cell", "NV-C100", "WO-42", "R1", "operator");

    private static ProcessStepStarted Start(string site, string serial) =>
        new(Guid.NewGuid(), Now, Now, site, serial, "Formation", "run-1", "equipment-1", "operator");

    private static UnitMeasurementRecorded Measure(string site, string serial) =>
        new(Guid.NewGuid(), Now, Now, site, serial, "Formation", "run-1", "equipment-1",
            "OCV", 3.6m, "V", "operator");

    private static ProcessStepCompleted Complete(string site, string serial) =>
        new(Guid.NewGuid(), Now, Now, site, serial, "Formation", "run-1", "operator");

    private sealed class Feed : IGlobalEventFeed
    {
        public List<StoredStreamEvent> Events { get; } = [];

        public Task<IReadOnlyList<StoredStreamEvent>> ReadAsync(string siteId, long afterGlobalSequence,
            int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StoredStreamEvent>>(
                Events.Where(e => e.SiteId == siteId && e.GlobalSequence > afterGlobalSequence)
                    .OrderBy(e => e.GlobalSequence).Take(limit).ToArray());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _postgres;
        public NpgsqlDataSource DataSource { get; }

        private Fixture(PostgreSqlContainer postgres, NpgsqlDataSource dataSource)
        { _postgres = postgres; DataSource = dataSource; }

        public NpgsqlDataSource CreateRuntimeDataSource() => NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
            { Options = "-c role=nvm_projection" }.ConnectionString);

        public static async Task<Fixture> StartAsync()
        {
            var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await postgres.StartAsync(Ct);
            var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
            await ProjectionSchemaMigrator.UpgradeAsync(dataSource, Ct);
            return new Fixture(postgres, dataSource);
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var command = DataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync(Ct);
        }

        public async Task<long> CountAsync(string site)
        {
            await using var command = DataSource.CreateCommand(
                "SELECT count(*) FROM rm.unit_current WHERE site_id = @site;");
            command.Parameters.AddWithValue("site", site);
            return (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        public async Task<long> CheckpointAsync(string site)
        {
            await using var command = DataSource.CreateCommand("""
                SELECT last_global_seq FROM rm.projection_checkpoint
                WHERE site_id = @site AND projection_name = @name;
                """);
            command.Parameters.AddWithValue("site", site);
            command.Parameters.AddWithValue("name", ProductionUnitProjection.Name);
            return (long?)await command.ExecuteScalarAsync(Ct) ?? 0;
        }

        public async Task<string> JsonAsync(string site)
        {
            await using var command = DataSource.CreateCommand("""
                SELECT coalesce(jsonb_agg(to_jsonb(u) ORDER BY serial_number)::text, '[]')
                FROM rm.unit_current AS u WHERE site_id = @site;
                """);
            command.Parameters.AddWithValue("site", site);
            return (string)(await command.ExecuteScalarAsync(Ct))!;
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await _postgres.DisposeAsync();
        }
    }
}
