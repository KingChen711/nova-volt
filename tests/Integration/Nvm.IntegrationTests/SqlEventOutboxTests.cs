using System.Collections.Immutable;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Nvm.CommandStore;
using Nvm.EventStore;
using Nvm.Kernel.Commands.Idempotency;
using Nvm.Kernel.EventSourcing;

namespace Nvm.IntegrationTests;

public sealed class SqlEventOutboxTests : IClassFixture<SqlCommandStoreFixture>, IAsyncLifetime
{
    private readonly SqlCommandStoreFixture _fixture;
    private readonly string _database = "outbox_test_" + Guid.NewGuid().ToString("N");
    private string ConnectionString => new SqlConnectionStringBuilder(_fixture.ConnectionString)
    { InitialCatalog = _database }.ConnectionString;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SqlEventOutboxTests(SqlCommandStoreFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        // A shared container is cheap, but each test must own its queue contents. An append-only
        // test leaves an undispatched row; sharing that row made the suite depend on test order.
        await _fixture.ExecuteAsync("""
            DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@database);
            EXEC(@sql);
            """, command => command.Parameters.AddWithValue("database", _database), Ct);
        await CommandSchemaMigrator.UpgradeAsync(ConnectionString, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        using (var pool = new SqlConnection(ConnectionString))
        { SqlConnection.ClearPool(pool); }
        await _fixture.ExecuteAsync("""
            DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database)
                + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@database);
            EXEC(@sql);
            """, command => command.Parameters.AddWithValue("database", _database), CancellationToken.None);
    }

    private async Task<CollectionOutcome> SubmitAsync(RecordDataCollection command,
        Func<SqlCommandSession, Task<CollectionOutcome>> handler)
    {
        await using var session = new SqlCommandSession();
        var store = new SqlIdempotencyStore(session,
            new SqlCommandStoreOptions { ConnectionString = ConnectionString }, _fixture.SharedInMemory);
        var behavior = new IdempotencyBehavior<RecordDataCollection, CollectionOutcome>(store, TimeProvider.System);
        return await behavior.HandleAsync(command, () => handler(session), Ct);
    }

    [Fact]
    public async Task AppendAndOutboxCommitTogether_AndHandlerRollbackRemovesBoth()
    {
        await EventSchemaMigrator.UpgradeAsync(ConnectionString, Ct);
        var fact = Fact();
        var stream = Stream();
        await AppendAsync(stream, fact);
        (await CountAsync("es.Events", fact.SourceEventId)).ShouldBe(1);
        (await CountAsync("es.Outbox", fact.SourceEventId)).ShouldBe(1);

        var rolledBack = Fact();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            SubmitAsync(Command("forced rollback"), async session =>
            {
                await Store(session).AppendAsync("NV1", Stream(), "ProductionUnit", 0,
                    ImmutableArray.Create(rolledBack), Ct);
                throw new InvalidOperationException("Handler aborted before commit");
            }));
        (await CountAsync("es.Events", rolledBack.SourceEventId)).ShouldBe(0);
        (await CountAsync("es.Outbox", rolledBack.SourceEventId)).ShouldBe(0);
    }

    [Fact]
    public async Task BrokerFailure_RetryUsesSameEventId_AndDoesNotAddAnotherOutboxRow()
    {
        await EventSchemaMigrator.UpgradeAsync(ConnectionString, Ct);
        var fact = Fact();
        await AppendAsync(Stream(), fact);
        var clock = new FakeTimeProvider(fact.RecordedAt.AddSeconds(1));
        var publisher = new RecordingPublisher { FailNext = true };
        var dispatcher = Dispatcher(publisher, clock);

        (await dispatcher.DispatchOnceAsync(1, Ct)).ShouldBe(new OutboxDispatchResult(1, 0, 1));
        publisher.Attempted.ShouldBe([fact.SourceEventId]);
        (await dispatcher.DispatchOnceAsync(1, Ct)).Claimed.ShouldBe(0);
        clock.Advance(TimeSpan.FromSeconds(5));
        (await Dispatcher(publisher, clock).DispatchOnceAsync(1, Ct))
            .ShouldBe(new OutboxDispatchResult(1, 1, 0));
        publisher.Attempted.ShouldBe([fact.SourceEventId, fact.SourceEventId]);
        (await CountAsync("es.Outbox", fact.SourceEventId)).ShouldBe(1);
        (await dispatcher.DispatchOnceAsync(1, Ct)).Claimed.ShouldBe(0);
    }

    [Fact]
    public async Task CrashAfterBrokerAcceptedBeforeAck_ReclaimsLeaseWithStableIdentity()
    {
        await EventSchemaMigrator.UpgradeAsync(ConnectionString, Ct);
        var fact = Fact();
        await AppendAsync(Stream(), fact);
        var clock = new FakeTimeProvider(fact.RecordedAt.AddSeconds(1));
        using var stop = new CancellationTokenSource();
        var publisher = new RecordingPublisher { CancelAfterAccept = stop };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Dispatcher(publisher, clock).DispatchOnceAsync(1, stop.Token));
        publisher.Accepted.ShouldBe([fact.SourceEventId]);
        (await Dispatcher(publisher, clock).DispatchOnceAsync(1, Ct)).Claimed.ShouldBe(0);

        clock.Advance(TimeSpan.FromMinutes(2));
        (await Dispatcher(publisher, clock).DispatchOnceAsync(1, Ct))
            .ShouldBe(new OutboxDispatchResult(1, 1, 0));
        publisher.Attempted.ShouldBe([fact.SourceEventId, fact.SourceEventId]);
        publisher.DistinctAccepted.ShouldBe(1);
        (await CountAsync("es.Outbox", fact.SourceEventId)).ShouldBe(1);
    }

    [Fact]
    public async Task PartialBatchFailure_DoesNotReplayAcknowledgedEvent()
    {
        await EventSchemaMigrator.UpgradeAsync(ConnectionString, Ct);
        var first = Fact();
        var second = Fact();
        await AppendAsync(Stream(), first, second);
        var clock = new FakeTimeProvider(second.RecordedAt.AddSeconds(1));
        var publisher = new RecordingPublisher { FailEventId = second.SourceEventId };
        (await Dispatcher(publisher, clock).DispatchOnceAsync(2, Ct))
            .ShouldBe(new OutboxDispatchResult(2, 1, 1));
        clock.Advance(TimeSpan.FromSeconds(5));
        publisher.FailEventId = null;
        (await Dispatcher(publisher, clock).DispatchOnceAsync(2, Ct))
            .ShouldBe(new OutboxDispatchResult(1, 1, 0));
        publisher.Attempted.Count(id => id == first.SourceEventId).ShouldBe(1);
        publisher.Attempted.Count(id => id == second.SourceEventId).ShouldBe(2);
    }

    [Fact]
    public async Task SlowPublisher_DoesNotLeaseWaitingBatch_AndTimesOutBeforeLeaseExpires()
    {
        await EventSchemaMigrator.UpgradeAsync(ConnectionString, Ct);
        var first = Fact();
        var second = Fact() with { RecordedAt = first.RecordedAt.AddTicks(1) };
        await AppendAsync(Stream(), first, second);
        var clock = new FakeTimeProvider(second.RecordedAt.AddSeconds(1));
        var slow = new BlockingPublisher();
        var work = Dispatcher(slow, clock).DispatchOnceAsync(2, Ct);
        await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var peer = new RecordingPublisher();
        (await Dispatcher(peer, clock).DispatchOnceAsync(2, Ct))
            .ShouldBe(new OutboxDispatchResult(1, 1, 0));
        peer.Accepted.ShouldBe([second.SourceEventId]);
        clock.Advance(TimeSpan.FromMinutes(1));
        (await work.WaitAsync(TimeSpan.FromSeconds(10), Ct))
            .ShouldBe(new OutboxDispatchResult(1, 0, 1));
        slow.Cancelled.ShouldBeTrue();

        clock.Advance(TimeSpan.FromSeconds(5));
        (await Dispatcher(peer, clock).DispatchOnceAsync(2, Ct))
            .ShouldBe(new OutboxDispatchResult(1, 1, 0));
        peer.DistinctAccepted.ShouldBe(2);
    }

    private sealed class BlockingPublisher : IEventOutboxPublisher
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task PublishAsync(OutboxEvent message, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }

    private SqlEventOutboxDispatcher Dispatcher(IEventOutboxPublisher publisher, TimeProvider clock) =>
        new(new SqlEventStoreOptions { ConnectionString = ConnectionString }, publisher, clock);

    private async Task AppendAsync(string stream, params NewStreamEvent[] facts)
    {
        await SubmitAsync(Command("outbox append"), async session =>
        {
            await Store(session).AppendAsync("NV1", stream, "ProductionUnit", 0,
                facts.ToImmutableArray(), Ct);
            return new CollectionOutcome(true, "OK");
        });
    }

    private SqlEventStore Store(Nvm.CommandStore.SqlCommandSession session) =>
        new(session, new SqlEventStoreOptions { ConnectionString = ConnectionString },
            new EventUpcasterChain([]));

    private static RecordDataCollection Command(string payload) =>
        SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", Guid.NewGuid().ToString("N"), payload);

    private static string Stream() => "outbox-" + Guid.NewGuid().ToString("N");

    private static NewStreamEvent Fact()
    {
        var now = TimeProvider.System.GetUtcNow();
        return new NewStreamEvent(Guid.NewGuid(), "com.novavolt.traceability.unit-serialized.v1", 1,
            "{\"siteId\":\"NV1\",\"kind\":\"Cell\"}", "{}", now, now);
    }

    private async Task<int> CountAsync(string table, Guid eventId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        // Only the two constant test table names are accepted; event identity is parameterized.
        var sql = table switch
        {
            "es.Events" => "SELECT COUNT(*) FROM es.Events WHERE SourceEventId = @id;",
            "es.Outbox" => "SELECT COUNT(*) FROM es.Outbox WHERE EventId = @id;",
            _ => throw new ArgumentException("Unknown test table", nameof(table)),
        };
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = eventId;
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class RecordingPublisher : IEventOutboxPublisher
    {
        public List<Guid> Attempted { get; } = [];
        public HashSet<Guid> Accepted { get; } = [];
        public int DistinctAccepted => Accepted.Count;
        public bool FailNext { get; set; }
        public Guid? FailEventId { get; set; }
        public CancellationTokenSource? CancelAfterAccept { get; set; }

        public async Task PublishAsync(OutboxEvent message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempted.Add(message.EventId);
            if (FailNext || FailEventId == message.EventId)
            {
                FailNext = false;
                throw new InvalidOperationException("Broker unavailable");
            }
            Accepted.Add(message.EventId);
            if (CancelAfterAccept is { } stop)
            {
                CancelAfterAccept = null;
                await stop.CancelAsync();
                throw new OperationCanceledException(stop.Token);
            }
        }
    }
}
