using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Contracts.Events.Traceability;
using Nvm.Kernel.EventSourcing;
using Nvm.Projections;
using Nvm.PublicObjectModel;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>Genealogy read model: DAG có thời gian, closure đếm đường đi, span trên cuộn, append-only.</summary>
public sealed class GenealogyProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 1, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string C1 = "NV1CL16269A00001";
    private const string C2 = "NV1CL16269A00002";
    private const string C3 = "NV1CL16269A00003";
    private const string C4 = "NV1CL16269A00004";
    private const string M1 = "NV1MM16269A00001";
    private const string M2 = "NV1MM16269A00002";
    private const string M3 = "NV1MM16269A00003";
    private const string P1 = "NV1PP16269A00001";
    private const string P2 = "NV1PP16269A00002";

    [Fact]
    public async Task ForwardBackwardSpanUnlinkAndCorrection_MatchGroundTruth_AndRebuildIsIdentical()
    {
        await using var fixture = await Fixture.StartAsync();
        var feed = new Feed();
        var projection = new GenealogyProjection(fixture.DataSource, feed);
        var queries = new TraceQueries(fixture.DataSource);

        feed.Add("roll:ROLL-A", new RollCoated(Id(), Now, Now, "NV1", "ROLL-A",
            [new("A", 0m, 1250m, "SLR-1", "FOIL-1", "RCP-1", "COAT-01"),
             new("A", 1250m, 1430m, "SLR-2", "FOIL-1", "RCP-1", "COAT-01"),
             new("A", 1430m, 3000m, "SLR-3", "FOIL-1", "RCP-1", "COAT-01")], "op"));
        // Cell 1-2 lấy đoạn lỗi 1250–1430; cell 3-4 lấy đoạn khác của cùng cuộn.
        Consume(feed, C1, "ROLL-A", "Roll", 1260m, 1261m);
        Consume(feed, C2, "ROLL-A", "Roll", 1400m, 1401m);
        Consume(feed, C3, "ROLL-A", "Roll", 2000m, 2001m);
        Consume(feed, C4, "ROLL-A", "Roll", 2100m, 2101m);
        Consume(feed, C1, "LOT-E-001", "Lot");
        Consume(feed, C2, "LOT-E-001", "Lot");
        Consume(feed, C3, "LOT-E-002", "Lot");
        Consume(feed, C4, "LOT-E-002", "Lot");
        Assemble(feed, C1, M1);
        Assemble(feed, C2, M1);
        Assemble(feed, C3, M2);
        Assemble(feed, C4, M2);
        Assemble(feed, M1, P1);
        Assemble(feed, M2, P2);

        await projection.CatchUpAsync("NV1", batchSize: 3, cancellationToken: Ct);
        await projection.CatchUpAsync("NV1", cancellationToken: Ct);   // chạy lại là no-op

        (await Ids(queries.ForwardAsync("NV1", 1, "LOT-E-001", 4, Ct))).ShouldBe([P1]);
        (await Ids(queries.ForwardAsync("NV1", 1, "LOT-E-002", 4, Ct))).ShouldBe([P2]);
        (await Ids(queries.ForwardAsync("NV1", 5, "ROLL-A", 4, Ct))).ShouldBe([P1, P2]);
        (await Ids(queries.BackwardAsync("NV1", 4, P1, 1, Ct))).ShouldBe(["LOT-E-001"]);
        (await Ids(queries.RollSpanAsync("NV1", "ROLL-A", 1250m, 1430m, Ct))).ShouldBe([C1, C2]);
        (await Ids(queries.ForwardByRecursionAsync("NV1", 1, "LOT-E-001", 4, Ct))).ShouldBe([P1]);
        (await Ids(queries.BackwardByRecursionAsync("NV1", 4, P2, 1, Ct))).ShouldBe(["LOT-E-002"]);
        (await Ids(queries.ForwardAsync("DE1", 1, "LOT-E-001", null, Ct))).ShouldBeEmpty();

        // Rework: tháo C2 khỏi M1 → lot E-001 vẫn tới P1 qua C1 (DAG, đếm đường đi).
        feed.Add("membership:" + C2, new UnitRemovedFrom(Id(), Now, Now, "NV1", C2, M1, "REWORK", "run", "op"));
        await projection.CatchUpAsync("NV1", cancellationToken: Ct);
        (await Ids(queries.ForwardAsync("NV1", 2, C2, null, Ct))).ShouldBeEmpty();
        (await Ids(queries.ForwardAsync("NV1", 1, "LOT-E-001", 4, Ct))).ShouldBe([P1]);
        (await PathsAsync(fixture, "LOT-E-001", P1)).ShouldBe(1);
        // Sửa sai: C3 thực ra nằm trong M3, không phải M2. Cạnh cũ được thay, không bị xoá.
        Assemble(feed, M3, P1);
        feed.Add("membership:" + C3, new GenealogyCorrectionRecorded(Id(), Now, Now, "NV1", C3, M2, M3, "S03",
            "Quét nhầm module", "qa"));
        await projection.CatchUpAsync("NV1", cancellationToken: Ct);
        (await Ids(queries.ForwardAsync("NV1", 2, C3, 4, Ct))).ShouldBe([P1]);
        (await Ids(queries.BackwardAsync("NV1", 4, P1, 2, Ct))).ShouldBe([C1, C3]);
        (await Ids(queries.ForwardAsync("NV1", 1, "LOT-E-002", 4, Ct))).ShouldBe([P1, P2]);
        await using (var history = fixture.DataSource.CreateCommand("""
            SELECT count(*) FILTER (WHERE unlinked_at IS NOT NULL AND superseded_by IS NOT NULL),
                   count(*) FILTER (WHERE edge_kind = 5 AND unlinked_at IS NULL)
            FROM trace.genealogy_link WHERE parent_id = 'NV1CL16269A00003' AND edge_kind IN (2, 5);
            """))
        await using (var reader = await history.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            reader.GetInt64(0).ShouldBe(1);
            reader.GetInt64(1).ShouldBe(1);
        }

        var incremental = await ClosureAsync(fixture);
        var incrementalLinks = await LinksAsync(fixture);
        var report = await projection.RebuildAsync("NV1", batchSize: 4, cancellationToken: Ct);
        report.FactsApplied.ShouldBe(feed.Events.Count);
        (await ClosureAsync(fixture)).ShouldBe(incremental);
        (await LinksAsync(fixture)).ShouldBe(incrementalLinks);

        // Lab M6 #2: projection "INSERT thay vì UPSERT" chạy lại cùng fact → unique source_event_id chặn,
        // không có dòng trùng nào lọt vào read model.
        await using (var naive = fixture.DataSource.CreateCommand("""
            INSERT INTO trace.genealogy_link (site_id, edge_kind, parent_type, parent_id, child_type, child_id,
                operation_run_id, linked_at, recorded_at, source_event_id)
            SELECT site_id, edge_kind, parent_type, parent_id, child_type, child_id, operation_run_id, linked_at,
                recorded_at, source_event_id FROM trace.genealogy_link LIMIT 1;
            """))
        {
            var error = await Should.ThrowAsync<PostgresException>(() => naive.ExecuteNonQueryAsync(Ct));
            error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }
    }

    [Fact]
    public async Task OverlappingRollSegment_IsRejectedByExclusionConstraint()
    {
        await using var fixture = await Fixture.StartAsync();
        var feed = new Feed();
        var projection = new GenealogyProjection(fixture.DataSource, feed);
        feed.Add("roll:ROLL-B", new RollCoated(Id(), Now, Now, "NV1", "ROLL-B",
            [new("A", 0m, 100m, "S", "F", "R", "E"), new("B", 50m, 150m, "S", "F", "R", "E")], "op"));
        await projection.CatchUpAsync("NV1", cancellationToken: Ct);
        await using var overlap = fixture.DataSource.CreateCommand("""
            INSERT INTO trace.roll_segment (site_id, roll_id, web_side, span, slurry_batch_id, foil_lot_id,
                recipe_version_id, equipment_id, recorded_at, source_event_id)
            VALUES ('NV1', 'ROLL-B', 'A', numrange(99, 120), 'S', 'F', 'R', 'E', now(), gen_random_uuid());
            """);
        var error = await Should.ThrowAsync<PostgresException>(() => overlap.ExecuteNonQueryAsync(Ct));
        error.SqlState.ShouldBe(PostgresErrorCodes.ExclusionViolation);
    }

    [Fact]
    public async Task RuntimeRole_CannotUpdateOrDeleteLinks_ButInboxDeliversInStreamOrder()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.ExecuteAsync("CREATE ROLE nvm_projection NOLOGIN;");
        await ProjectionSchemaMigrator.UpgradeAsync(fixture.DataSource, Ct);
        await using var runtime = fixture.CreateRuntimeDataSource();
        var inbox = new GenealogyProjectionInbox(runtime);
        var assembled = new UnitAssembledInto(Id(), Now, Now, "NV1", C1, M1, "S01", "run", "op");
        var removed = new UnitRemovedFrom(Id(), Now, Now, "NV1", C1, M1, "REWORK", "run", "op");
        // Tháo tới trước lắp: inbox giữ lại, không áp dụng khi version trước chưa có.
        await inbox.EnqueueAsync(Stored(2, "membership:" + C1, 2, removed), Ct);
        (await inbox.DispatchAsync("NV1", cancellationToken: Ct)).ShouldBe(0);
        await inbox.EnqueueAsync(Stored(1, "membership:" + C1, 1, assembled), Ct);
        await inbox.EnqueueAsync(Stored(1, "membership:" + C1, 1, assembled), Ct);
        (await inbox.DispatchAsync("NV1", cancellationToken: Ct)).ShouldBe(1);
        (await inbox.DispatchAsync("NV1", cancellationToken: Ct)).ShouldBe(1);
        (await inbox.DispatchAsync("NV1", cancellationToken: Ct)).ShouldBe(0);

        await using (var active = fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM trace.genealogy_link WHERE unlinked_at IS NULL;"))
        { (await active.ExecuteScalarAsync(Ct)).ShouldBe(0L); }
        foreach (var sql in new[] { "UPDATE trace.genealogy_link SET child_id = 'x';", "DELETE FROM trace.genealogy_link;",
            "SELECT trace.reset_site('NV1');" })
        {
            await using var tamper = runtime.CreateCommand(sql);
            var error = await Should.ThrowAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync(Ct));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }

    private static Guid Id() => Guid.NewGuid();

    private static void Consume(Feed feed, string serial, string lot, string kind, decimal? from = null, decimal? to = null) =>
        feed.Add("consumption:" + serial, new MaterialLotConsumed(Id(), Now, Now, "NV1", lot, kind,
            kind == "Roll" ? "ANODE-ROLL" : "ELECTROLYTE", serial, 1m, kind == "Roll" ? "m" : "g", from, to, "run", "op"));

    private static void Assemble(Feed feed, string child, string parent) =>
        feed.Add("membership:" + child, new UnitAssembledInto(Id(), Now, Now, "NV1", child, parent, "S01", "run", "op"));

    private static async Task<string[]> Ids(Task<IReadOnlyList<TraceNode>> nodes) =>
        [.. (await nodes).Select(node => node.Id)];

    private static async Task<long> PathsAsync(Fixture fixture, string ancestor, string descendant)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT paths FROM rm.genealogy_closure WHERE ancestor_id = @a AND descendant_id = @d;");
        command.Parameters.AddWithValue("a", ancestor);
        command.Parameters.AddWithValue("d", descendant);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Bảng cạnh theo nội dung, bỏ id tự tăng; cạnh thay thế được nêu bằng event nguồn của nó.</summary>
    private static async Task<string> LinksAsync(Fixture fixture)
    {
        await using var command = fixture.DataSource.CreateCommand("""
            SELECT string_agg(format('%s|%s|%s|%s|%s|%s|%s|%s|%s|%s', l.edge_kind, l.parent_id, l.child_id,
                l.position, l.span, l.quantity, l.unlinked_at, l.unlink_event_id, s.source_event_id, l.source_event_id),
                ';' ORDER BY l.source_event_id)
            FROM trace.genealogy_link l LEFT JOIN trace.genealogy_link s ON s.id = l.superseded_by
            WHERE l.site_id = 'NV1';
            """);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<string> ClosureAsync(Fixture fixture)
    {
        await using var command = fixture.DataSource.CreateCommand("""
            SELECT coalesce(string_agg(format('%s|%s|%s|%s|%s', ancestor_type, ancestor_id, descendant_type,
                descendant_id, paths), ';' ORDER BY ancestor_type, ancestor_id, descendant_type, descendant_id), '')
            FROM rm.genealogy_closure WHERE site_id = 'NV1';
            """);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    internal static StoredStreamEvent Stored(long sequence, string stream, long version, IDomainEvent value)
    {
        var type = EventTypeName.Of(value.GetType());
        return new StoredStreamEvent(sequence, value.SiteId, stream, version, value.EventId, type.Value, 1,
            JsonSerializer.Serialize(value, value.GetType(), Json), "{}", value.OccurredAt, Now);
    }

    private sealed class Feed : IGlobalEventFeed
    {
        private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
        public List<StoredStreamEvent> Events { get; } = [];

        public void Add(string stream, IDomainEvent value)
        {
            var version = _versions[stream] = _versions.GetValueOrDefault(stream) + 1;
            Events.Add(Stored(Events.Count + 1, stream, version, value));
        }

        public Task<IReadOnlyList<StoredStreamEvent>> ReadAsync(string siteId, long afterGlobalSequence, int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<StoredStreamEvent>>(
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

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await _postgres.DisposeAsync();
        }
    }
}
