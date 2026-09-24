using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Nvm.EventStore;
using Nvm.Kernel.EventSourcing;

namespace Nvm.IntegrationTests;

/// <summary>Real SQL Server checks for append concurrency, replay, snapshot, site scope and DB immutability.</summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class EventStoreTests : IClassFixture<SqlCommandStoreFixture>
{
    private readonly SqlCommandStoreFixture _fixture;
    private readonly ITestOutputHelper _output;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public EventStoreTests(SqlCommandStoreFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task AppendAndRead_PreserveOrderAndSite_AndExactRetryDoesNotDuplicate()
    {
        await EventSchemaMigrator.UpgradeAsync(_fixture.ConnectionString, Ct);
        var stream = "cell-" + Guid.NewGuid().ToString("N");
        var events = ImmutableArray.Create(
            Fact("a", """{"subject":"urn:trace-unit:cell:NV1CL16238A00123","correlationid":"WO-2026-0042"}"""),
            Fact("b"));
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", Guid.NewGuid().ToString("N"), "append two");
        await _fixture.SubmitAsync(command, async session =>
        {
            var store = Store(session);
            (await store.AppendAsync("NV1", stream, "ProductionUnit", 0, events, Ct)).ShouldBe(2);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);

        var reader = Store(new());
        var result = await reader.ReadStreamAsync("NV1", stream, Ct);
        result.ShouldNotBeNull();
        result.Version.ShouldBe(2);
        result.Events.Select(e => e.Version).ShouldBe([1L, 2L]);
        result.Events.Select(e => e.SourceEventId).ShouldBe(events.Select(e => e.SourceEventId));
        using (var envelope = System.Text.Json.JsonDocument.Parse(result.Events[0].EnvelopeJson))
        {
            var root = envelope.RootElement;
            root.GetProperty("specversion").GetString().ShouldBe("1.0");
            root.GetProperty("id").GetGuid().ShouldBe(events[0].SourceEventId);
            root.GetProperty("type").GetString().ShouldBe(events[0].EventType);
            root.GetProperty("source").GetString().ShouldBe("urn:novavolt:nv1:app-execution");
            root.GetProperty("datacontenttype").GetString().ShouldBe("application/json");
            root.GetProperty("subject").GetString().ShouldBe("urn:trace-unit:cell:NV1CL16238A00123");
            root.GetProperty("correlationid").GetString().ShouldBe("WO-2026-0042");
            root.GetProperty("data").GetProperty("siteId").GetString().ShouldBe("NV1");
        }
        using (var envelope = System.Text.Json.JsonDocument.Parse(result.Events[1].EnvelopeJson))
        {
            envelope.RootElement.TryGetProperty("subject", out _).ShouldBeFalse();
            envelope.RootElement.TryGetProperty("correlationid", out _).ShouldBeFalse();
        }
        (await reader.ReadStreamAsync("DE1", stream, Ct)).ShouldBeNull();

        // A new durable command can retry the same exact facts after a lost response.
        var retry = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", Guid.NewGuid().ToString("N"), "retry exact facts");
        await _fixture.SubmitAsync(retry, async session =>
        {
            (await Store(session).AppendAsync("NV1", stream, "ProductionUnit", 0, events, Ct)).ShouldBe(2);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);
        (await reader.ReadStreamAsync("NV1", stream, Ct))!.Events.Length.ShouldBe(2);
    }

    [Fact]
    public async Task ConcurrentSameExpectedVersion_OneWinsAndOtherCanRetryAtNewVersion()
    {
        await EventSchemaMigrator.UpgradeAsync(_fixture.ConnectionString, Ct);
        var stream = "cell-" + Guid.NewGuid().ToString("N");
        async Task<(bool Won, long Actual)> AttemptAsync(string payload)
        {
            var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", Guid.NewGuid().ToString("N"), payload);
            try
            {
                await _fixture.SubmitAsync(command, async session =>
                {
                    await Store(session).AppendAsync("NV1", stream, "ProductionUnit", 0,
                        ImmutableArray.Create(Fact(payload)), Ct);
                    return new CollectionOutcome(true, "OK");
                }, cancellationToken: Ct);
                return (true, 0);
            }
            catch (EventConcurrencyException error) { return (false, error.ActualVersion); }
        }

        var attempts = await Task.WhenAll(AttemptAsync("first"), AttemptAsync("second"));
        attempts.Count(item => item.Won).ShouldBe(1);
        attempts.Single(item => !item.Won).Actual.ShouldBe(1);
        var retryCommand = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", Guid.NewGuid().ToString("N"), "retry at v1");
        await _fixture.SubmitAsync(retryCommand, async session =>
        {
            (await Store(session).AppendAsync("NV1", stream, "ProductionUnit", 1,
                ImmutableArray.Create(Fact("retried")), Ct)).ShouldBe(2);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);
        (await Store(new()).ReadStreamAsync("NV1", stream, Ct))!.Version.ShouldBe(2);
    }

    [Fact]
    public async Task SnapshotAtHundred_IsMonotonicAndReadBack()
    {
        await EventSchemaMigrator.UpgradeAsync(_fixture.ConnectionString, Ct);
        var stream = "cell-" + Guid.NewGuid().ToString("N");
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", Guid.NewGuid().ToString("N"), "100 facts");
        await _fixture.SubmitAsync(command, async session =>
        {
            var store = Store(session);
            var events = Enumerable.Range(1, 100).Select(i => Fact(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToImmutableArray();
            (await store.AppendAsync("NV1", stream, "ProductionUnit", 0, events, Ct)).ShouldBe(100);
            await store.SaveSnapshotAsync(new EventSnapshot("NV1", stream, 100, """{"state":"Made"}""",
                TimeProvider.System.GetUtcNow()), Ct);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);

        var snapshot = await Store(new()).ReadSnapshotAsync("NV1", stream, Ct);
        snapshot.ShouldNotBeNull();
        snapshot.Version.ShouldBe(100);
        (await Store(new()).ReadSnapshotAsync("DE1", stream, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task EventRows_DenyUpdateAndDeleteToRuntimePrincipal()
    {
        await _fixture.ExecuteAsync("IF DATABASE_PRINCIPAL_ID('nvm_app') IS NULL CREATE USER nvm_app WITHOUT LOGIN;", _ => { }, Ct);
        await EventSchemaMigrator.UpgradeAsync(_fixture.ConnectionString, Ct);
        var stream = "cell-" + Guid.NewGuid().ToString("N");
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", Guid.NewGuid().ToString("N"), "immutable event");
        await _fixture.SubmitAsync(command, async session =>
        {
            await Store(session).AppendAsync("NV1", stream, "ProductionUnit", 0,
                ImmutableArray.Create(Fact("immutable")), Ct);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);

        await Should.ThrowAsync<SqlException>(() => _fixture.ExecuteAsync("""
            EXECUTE AS USER = 'nvm_app';
            UPDATE es.Events SET PayloadJson = '{}' WHERE SiteId = 'NV1';
            REVERT;
            """, _ => { }, Ct));
        await Should.ThrowAsync<SqlException>(() => _fixture.ExecuteAsync("""
            EXECUTE AS USER = 'nvm_app';
            DELETE FROM es.Events WHERE SiteId = 'NV1';
            REVERT;
            """, _ => { }, Ct));
        (await Store(new()).ReadStreamAsync("NV1", stream, Ct))!.Events.Length.ShouldBe(1);
    }

    [Fact]
    public async Task SourceEventIdReusedAcrossStreams_RejectsAppendAndRollsBackStreamHeader()
    {
        await EventSchemaMigrator.UpgradeAsync(_fixture.ConnectionString, Ct);
        var sourceEvent = Fact("original");
        var firstStream = "cell-" + Guid.NewGuid().ToString("N");
        var conflictingStream = "cell-" + Guid.NewGuid().ToString("N");
        var firstCommand = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", Guid.NewGuid().ToString("N"), "first identity");
        await _fixture.SubmitAsync(firstCommand, async session =>
        {
            await Store(session).AppendAsync("NV1", firstStream, "ProductionUnit", 0,
                ImmutableArray.Create(sourceEvent), Ct);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);

        var secondCommand = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", Guid.NewGuid().ToString("N"), "duplicate identity");
        await Should.ThrowAsync<EventIdentityConflictException>(() => _fixture.SubmitAsync(secondCommand, async session =>
        {
            await Store(session).AppendAsync("NV1", conflictingStream, "ProductionUnit", 0,
                ImmutableArray.Create(sourceEvent), Ct);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct));

        (await Store(new()).ReadStreamAsync("NV1", conflictingStream, Ct)).ShouldBeNull();
        (await Store(new()).ReadStreamAsync("NV1", firstStream, Ct))!.Events.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Append1000EventsToOneStream_MeasuresPerAppendP95()
    {
        await EventSchemaMigrator.UpgradeAsync(_fixture.ConnectionString, Ct);
        var stream = "benchmark-cell-" + Guid.NewGuid().ToString("N");
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", Guid.NewGuid().ToString("N"), "event store p95");
        var events = Enumerable.Range(0, 1000).Select(i => Fact(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        var durations = new double[events.Length];
        await _fixture.SubmitAsync(command, async session =>
        {
            var store = Store(session);
            for (var i = 0; i < events.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await store.AppendAsync("NV1", stream, "ProductionUnit", i,
                    ImmutableArray.Create(events[i]), Ct);
                durations[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);

        Array.Sort(durations);
        var p95 = durations[(int)Math.Ceiling(durations.Length * 0.95) - 1];
        _output.WriteLine($"EVENT_STORE_APPEND count=1000 p95_ms={p95:F3} max_ms={durations[^1]:F3} " +
            "scope=AppendAsync_only transaction=one_command_session");
        Assert.True(p95 < 20, $"AppendAsync p95 was {p95:F3} ms for 1000 events; required <20 ms.");
    }

    private SqlEventStore Store(Nvm.CommandStore.SqlCommandSession session) =>
        new(session, new SqlEventStoreOptions { ConnectionString = _fixture.ConnectionString }, new EventUpcasterChain([]));

    private static NewStreamEvent Fact(string value, string metadata = "{}") => new(Guid.NewGuid(), "com.novavolt.traceability.unit-serialized.v1", 1,
        System.Text.Json.JsonSerializer.Serialize(new { siteId = "NV1", value }), metadata,
        TimeProvider.System.GetUtcNow(), TimeProvider.System.GetUtcNow());
}

/// <summary>Latency qualification must not compete with unrelated container startup/DDL storms.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EventStoreLatencyDefinition
{
    public const string Name = "Event store latency qualification";
}
