using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Contracts.Events.Traceability;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;

namespace Nvm.Projections;

/// <summary>
/// Read model genealogy: bảng cạnh append-only, đoạn cuộn và closure đếm số đường đi (scope §6.4, §8.2).
/// </summary>
/// <remarks>
/// Cạnh đi theo dòng vật liệu: <c>parent</c> là thượng nguồn (lot, cuộn, unit được lắp), <c>child</c> là hạ
/// nguồn (unit tiêu thụ, unit chứa). Vì vậy forward từ lot ra pack và backward từ pack về lot đều là một
/// lượt đọc closure theo một chiều.
/// Mỗi stream nguồn (<c>membership:*</c>, <c>consumption:*</c>, <c>roll:*</c>) được áp dụng đúng thứ tự
/// version qua <c>trace.stream_progress</c>; giữa các stream không cần thứ tự vì cộng/trừ số đường đi
/// trên closure giao hoán. Chạy lại cùng event là no-op.
/// </remarks>
public sealed class GenealogyProjection(NpgsqlDataSource dataSource, IGlobalEventFeed feed)
{
    public const string Name = "genealogy-v1";
    internal const string Consumed = "com.novavolt.material.material-lot-consumed.v1";
    internal const string Assembled = "com.novavolt.traceability.unit-assembled-into.v1";
    internal const string Removed = "com.novavolt.traceability.unit-removed-from.v1";
    internal const string Corrected = "com.novavolt.traceability.genealogy-correction-recorded.v1";
    internal const string Coated = "com.novavolt.production-execution.roll-coated.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool Handles(string eventType) =>
        eventType is Consumed or Assembled or Removed or Corrected or Coated;

    /// <summary>Loại node trong đồ thị: 1 lot, 2 cell, 3 module, 4 pack, 5 roll.</summary>
    public static short UnitNodeType(string serialNumber) => SerialNumber.Parse(serialNumber).Kind switch
    {
        ProductionUnitKind.Cell => 2,
        ProductionUnitKind.Module => 3,
        ProductionUnitKind.Pack => 4,
        var kind => throw new InvalidDataException($"Unknown unit kind {kind}.")
    };

    /// <summary>Đối chiếu: quét toàn bộ feed của site, áp dụng fact còn thiếu theo thứ tự stream.</summary>
    public async Task<long> CatchUpAsync(string siteId, int batchSize = 1000, CancellationToken cancellationToken = default)
    {
        ProjectionIdentity.ValidateSite(siteId);
        long cursor = 0;
        while (true)
        {
            var batch = await feed.ReadAsync(siteId, cursor, batchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            { break; }
            var handled = batch.Where(fact => Handles(fact.EventType)).ToList();
            if (handled.Count > 0)
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await LockAsync(connection, transaction, siteId, cancellationToken).ConfigureAwait(false);
                foreach (var fact in handled)
                { await ApplyAsync(connection, transaction, fact, cancellationToken).ConfigureAwait(false); }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            cursor = batch[^1].GlobalSequence;
            if (batch.Count < batchSize)
            { break; }
        }
        return cursor;
    }

    /// <summary>
    /// Xoá read model genealogy của site rồi dựng lại set-based: nạp cạnh theo thứ tự feed, sau đó tính
    /// closure một lần bằng recursive CTE. Cần credential chủ schema (runtime role không xoá được).
    /// </summary>
    public async Task<GenealogyRebuildReport> RebuildAsync(string siteId, int batchSize = 5000,
        CancellationToken cancellationToken = default)
    {
        ProjectionIdentity.ValidateSite(siteId);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, siteId, cancellationToken).ConfigureAwait(false);
        await using (var reset = new NpgsqlCommand("SELECT trace.reset_site(@site);", connection, transaction))
        {
            reset.Parameters.AddWithValue("site", siteId);
            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var bulk = new GenealogyBulkRebuild(siteId);
        long cursor = 0;
        while (true)
        {
            var batch = await feed.ReadAsync(siteId, cursor, batchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            { break; }
            foreach (var fact in batch.Where(fact => Handles(fact.EventType)))
            { bulk.Add(fact); }
            cursor = batch[^1].GlobalSequence;
            if (batch.Count < batchSize)
            { break; }
        }
        await bulk.WriteAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var facts = bulk.Facts;
        await using (var closure = new NpgsqlCommand(ClosureFromLinksSql, connection, transaction) { CommandTimeout = 0 })
        {
            closure.Parameters.AddWithValue("site", siteId);
            await closure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var inbox = new NpgsqlCommand("""
            UPDATE trace.inbox i SET applied = true FROM trace.stream_progress p
            WHERE i.site_id = @site AND p.site_id = i.site_id AND p.stream_id = i.stream_id
              AND i.stream_version <= p.stream_version AND NOT i.applied;
            """, connection, transaction))
        {
            inbox.Parameters.AddWithValue("site", siteId);
            await inbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GenealogyRebuildReport(siteId, facts, System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }

    /// <summary>Tính closure toàn site từ các cạnh đang hiệu lực; chỉ dùng khi closure của site đang rỗng.</summary>
    internal const string ClosureFromLinksSql = """
        WITH RECURSIVE edge AS (
            SELECT parent_type, parent_id, child_type, child_id, count(*)::bigint AS paths
            FROM trace.genealogy_link
            WHERE site_id = @site AND unlinked_at IS NULL AND edge_kind IN (1, 2, 4, 5)
            GROUP BY parent_type, parent_id, child_type, child_id
        ), walk(ancestor_type, ancestor_id, descendant_type, descendant_id, paths) AS (
            SELECT parent_type, parent_id, child_type, child_id, paths FROM edge
            UNION ALL
            SELECT w.ancestor_type, w.ancestor_id, e.child_type, e.child_id, w.paths * e.paths
            FROM walk w JOIN edge e ON e.parent_type = w.descendant_type AND e.parent_id = w.descendant_id
        )
        INSERT INTO rm.genealogy_closure (site_id, ancestor_type, ancestor_id, descendant_type, descendant_id, paths)
        SELECT @site, ancestor_type, ancestor_id, descendant_type, descendant_id, sum(paths)
        FROM walk GROUP BY ancestor_type, ancestor_id, descendant_type, descendant_id;
        """;

    internal static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO rm.projection_checkpoint(site_id, projection_name) VALUES (@site, @name)
            ON CONFLICT DO NOTHING;
            SELECT generation FROM rm.projection_checkpoint
            WHERE site_id = @site AND projection_name = @name FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("site", siteId);
        command.Parameters.AddWithValue("name", Name);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Áp dụng một fact nếu nó là version kế tiếp của stream; version cũ là no-op, khoảng trống là lỗi.</summary>
    internal static async Task<bool> ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        StoredStreamEvent fact, CancellationToken cancellationToken, bool maintainClosure = true)
    {
        if (fact.SchemaVersion != 1)
        { throw new InvalidDataException("Unsupported genealogy event schema version."); }
        long applied;
        await using (var progress = new NpgsqlCommand("""
            SELECT stream_version FROM trace.stream_progress
            WHERE site_id = @site AND stream_id = @stream FOR UPDATE;
            """, connection, transaction))
        {
            progress.Parameters.AddWithValue("site", fact.SiteId);
            progress.Parameters.AddWithValue("stream", fact.StreamId);
            applied = (long?)await progress.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        }
        if (applied >= fact.Version)
        { return false; }
        if (applied != fact.Version - 1)
        { throw new InvalidDataException("Genealogy stream version has a gap."); }

        var writer = new Writer(connection, transaction, fact, maintainClosure, cancellationToken);
        switch (fact.EventType)
        {
            case Consumed:
                var consumed = Read<MaterialLotConsumed>(fact);
                Ensure(consumed.SiteId, consumed.ConsumerSerialNumber, fact);
                await writer.LinkAsync(1, consumed.LotKind == "Roll" ? (short)5 : (short)1, consumed.LotId,
                    UnitNodeType(consumed.ConsumerSerialNumber), consumed.ConsumerSerialNumber, null,
                    consumed.SpanFromMeter is { } from && consumed.SpanToMeter is { } to ? new NpgsqlRange<decimal>(from, true, to, false) : null,
                    consumed.Quantity, consumed.UnitOfMeasure, consumed.OperationRunId, consumed.OccurredAt,
                    consumed.RecordedAt, fact.SourceEventId).ConfigureAwait(false);
                break;
            case Assembled:
                var assembled = Read<UnitAssembledInto>(fact);
                Ensure(assembled.SiteId, assembled.ChildSerialNumber, fact);
                // Cạnh đi theo dòng vật liệu: unit được lắp là thượng nguồn, unit chứa nó là hạ nguồn.
                await writer.LinkAsync(2, UnitNodeType(assembled.ChildSerialNumber), assembled.ChildSerialNumber,
                    UnitNodeType(assembled.ParentSerialNumber), assembled.ParentSerialNumber, assembled.Position, null,
                    null, null, assembled.OperationRunId, assembled.OccurredAt, assembled.RecordedAt,
                    fact.SourceEventId).ConfigureAwait(false);
                break;
            case Removed:
                var removed = Read<UnitRemovedFrom>(fact);
                Ensure(removed.SiteId, removed.ChildSerialNumber, fact);
                await writer.UnlinkAsync(removed.ChildSerialNumber, removed.ParentSerialNumber, removed.OccurredAt,
                    fact.SourceEventId, null).ConfigureAwait(false);
                break;
            case Corrected:
                var corrected = Read<GenealogyCorrectionRecorded>(fact);
                Ensure(corrected.SiteId, corrected.ChildSerialNumber, fact);
                var replacement = await writer.LinkAsync(5, UnitNodeType(corrected.ChildSerialNumber),
                    corrected.ChildSerialNumber, UnitNodeType(corrected.CorrectParentSerialNumber),
                    corrected.CorrectParentSerialNumber, corrected.Position, null, null, null, "CORRECTION",
                    corrected.OccurredAt, corrected.RecordedAt, fact.SourceEventId).ConfigureAwait(false);
                await writer.UnlinkAsync(corrected.ChildSerialNumber, corrected.WrongParentSerialNumber,
                    corrected.OccurredAt, fact.SourceEventId, replacement).ConfigureAwait(false);
                break;
            case Coated:
                var coated = Read<RollCoated>(fact);
                if (coated.SiteId != fact.SiteId || fact.StreamId != "roll:" + coated.RollId)
                { throw new InvalidDataException("Roll event belongs to another site or stream."); }
                foreach (var segment in coated.Segments)
                { await writer.SegmentAsync(coated.RollId, segment, coated.RecordedAt).ConfigureAwait(false); }
                break;
            default:
                throw new InvalidDataException($"Unsupported genealogy event: {fact.EventType}.");
        }

        await using var advance = new NpgsqlCommand("""
            INSERT INTO trace.stream_progress (site_id, stream_id, stream_version) VALUES (@site, @stream, @version)
            ON CONFLICT (site_id, stream_id) DO UPDATE SET stream_version = excluded.stream_version;
            """, connection, transaction);
        advance.Parameters.AddWithValue("site", fact.SiteId);
        advance.Parameters.AddWithValue("stream", fact.StreamId);
        advance.Parameters.AddWithValue("version", fact.Version);
        await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static T Read<T>(StoredStreamEvent fact) where T : Nvm.Contracts.Events.IDomainEvent
    {
        var value = JsonSerializer.Deserialize<T>(fact.PayloadJson, Json)
            ?? throw new InvalidDataException("Genealogy event payload is null.");
        if (value.EventId != fact.SourceEventId)
        { throw new InvalidDataException("Event ID differs from stored source event ID."); }
        return value;
    }

    private static void Ensure(string siteId, string serialNumber, StoredStreamEvent fact)
    {
        if (siteId != fact.SiteId || SerialNumber.Parse(serialNumber).SiteCode != fact.SiteId ||
            !fact.StreamId.EndsWith(":" + serialNumber, StringComparison.Ordinal))
        { throw new InvalidDataException("Genealogy event belongs to another site or stream."); }
    }

    private sealed class Writer(NpgsqlConnection connection, NpgsqlTransaction transaction, StoredStreamEvent fact,
        bool maintainClosure, CancellationToken cancellationToken)
    {
        public async Task<long> LinkAsync(short kind, short parentType, string parentId, short childType, string childId,
            string? position, NpgsqlRange<decimal>? span, decimal? quantity, string? uom, string operationRunId,
            DateTimeOffset linkedAt, DateTimeOffset recordedAt, Guid sourceEventId)
        {
            long id;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO trace.genealogy_link (site_id, edge_kind, parent_type, parent_id, child_type, child_id,
                    position, span, quantity, uom, operation_run_id, linked_at, recorded_at, source_event_id)
                VALUES (@site, @kind, @pt, @pid, @ct, @cid, @position, @span, @quantity, @uom, @run, @linked,
                    @recorded, @event)
                RETURNING id;
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("site", fact.SiteId);
                insert.Parameters.AddWithValue("kind", kind);
                insert.Parameters.AddWithValue("pt", parentType);
                insert.Parameters.AddWithValue("pid", parentId);
                insert.Parameters.AddWithValue("ct", childType);
                insert.Parameters.AddWithValue("cid", childId);
                insert.Parameters.AddWithValue("position", (object?)position ?? DBNull.Value);
                insert.Parameters.Add(new NpgsqlParameter("span", NpgsqlDbType.NumericRange)
                { Value = span is { } range ? range : DBNull.Value });
                insert.Parameters.AddWithValue("quantity", (object?)quantity ?? DBNull.Value);
                insert.Parameters.AddWithValue("uom", (object?)uom ?? DBNull.Value);
                insert.Parameters.AddWithValue("run", operationRunId);
                insert.Parameters.AddWithValue("linked", linkedAt);
                insert.Parameters.AddWithValue("recorded", recordedAt);
                insert.Parameters.AddWithValue("event", sourceEventId);
                id = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }
            if (maintainClosure)
            { await ClosureAsync(parentType, parentId, childType, childId, add: true).ConfigureAwait(false); }
            return id;
        }

        /// <summary>Gỡ cạnh lắp ráp đang hiệu lực từ unit thượng nguồn tới unit chứa nó.</summary>
        public async Task UnlinkAsync(string parentSerial, string childSerial, DateTimeOffset unlinkedAt, Guid eventId,
            long? supersededBy)
        {
            var parentType = UnitNodeType(parentSerial);
            var childType = UnitNodeType(childSerial);
            long link;
            await using (var find = new NpgsqlCommand("""
                SELECT id FROM trace.genealogy_link
                WHERE site_id = @site AND parent_type = @pt AND parent_id = @pid AND child_type = @ct
                  AND child_id = @cid AND edge_kind IN (2, 5) AND unlinked_at IS NULL
                ORDER BY id DESC LIMIT 1;
                """, connection, transaction))
            {
                find.Parameters.AddWithValue("site", fact.SiteId);
                find.Parameters.AddWithValue("pt", parentType);
                find.Parameters.AddWithValue("pid", parentSerial);
                find.Parameters.AddWithValue("ct", childType);
                find.Parameters.AddWithValue("cid", childSerial);
                link = (long?)await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("No active genealogy link to remove.");
            }
            if (maintainClosure)
            { await ClosureAsync(parentType, parentSerial, childType, childSerial, add: false).ConfigureAwait(false); }
            await using var unlink = new NpgsqlCommand("SELECT trace.unlink(@link, @at, @event, @superseded);",
                connection, transaction);
            unlink.Parameters.AddWithValue("link", link);
            unlink.Parameters.AddWithValue("at", unlinkedAt);
            unlink.Parameters.AddWithValue("event", eventId);
            unlink.Parameters.AddWithValue("superseded", (object?)supersededBy ?? DBNull.Value);
            await unlink.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task SegmentAsync(string rollId, RollSegment segment, DateTimeOffset recordedAt)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO trace.roll_segment (site_id, roll_id, web_side, span, slurry_batch_id, foil_lot_id,
                    recipe_version_id, equipment_id, recorded_at, source_event_id)
                VALUES (@site, @roll, @side, @span, @slurry, @foil, @recipe, @equipment, @recorded, @event);
                """, connection, transaction);
            insert.Parameters.AddWithValue("site", fact.SiteId);
            insert.Parameters.AddWithValue("roll", rollId);
            insert.Parameters.AddWithValue("side", segment.WebSide);
            insert.Parameters.Add(new NpgsqlParameter("span", NpgsqlDbType.NumericRange)
            { Value = new NpgsqlRange<decimal>(segment.FromMeter, true, segment.ToMeter, false) });
            insert.Parameters.AddWithValue("slurry", segment.SlurryBatchId);
            insert.Parameters.AddWithValue("foil", segment.FoilLotId);
            insert.Parameters.AddWithValue("recipe", segment.RecipeVersionId);
            insert.Parameters.AddWithValue("equipment", segment.EquipmentId);
            insert.Parameters.AddWithValue("recorded", recordedAt);
            insert.Parameters.AddWithValue("event", fact.SourceEventId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Cạnh P→C thêm/bớt anc(P)×desc(C) đường đi, tính trên closure trước thay đổi.</summary>
        private async Task ClosureAsync(short parentType, string parentId, short childType, string childId, bool add)
        {
            const string delta = """
                WITH anc AS (
                    SELECT ancestor_type AS t, ancestor_id AS id, paths FROM rm.genealogy_closure
                    WHERE site_id = @site AND descendant_type = @pt AND descendant_id = @pid
                    UNION ALL SELECT @pt, @pid, 1::bigint
                ), dsc AS (
                    SELECT descendant_type AS t, descendant_id AS id, paths FROM rm.genealogy_closure
                    WHERE site_id = @site AND ancestor_type = @ct AND ancestor_id = @cid
                    UNION ALL SELECT @ct, @cid, 1::bigint
                ), delta AS (
                    SELECT a.t AS at, a.id AS aid, d.t AS dt, d.id AS did, sum(a.paths * d.paths) AS p
                    FROM anc a CROSS JOIN dsc d GROUP BY a.t, a.id, d.t, d.id
                )
                """;
            var sql = add
                ? delta + """
                    INSERT INTO rm.genealogy_closure (site_id, ancestor_type, ancestor_id, descendant_type, descendant_id, paths)
                    SELECT @site, at, aid, dt, did, p FROM delta
                    ON CONFLICT (site_id, ancestor_type, ancestor_id, descendant_type, descendant_id)
                    DO UPDATE SET paths = rm.genealogy_closure.paths + excluded.paths;
                    """
                : delta + """
                    , gone AS (
                        DELETE FROM rm.genealogy_closure c USING delta
                        WHERE c.site_id = @site AND c.ancestor_type = delta.at AND c.ancestor_id = delta.aid
                          AND c.descendant_type = delta.dt AND c.descendant_id = delta.did AND c.paths <= delta.p
                        RETURNING 1)
                    UPDATE rm.genealogy_closure c SET paths = c.paths - delta.p FROM delta
                    WHERE c.site_id = @site AND c.ancestor_type = delta.at AND c.ancestor_id = delta.aid
                      AND c.descendant_type = delta.dt AND c.descendant_id = delta.did AND c.paths > delta.p;
                    """;
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("site", fact.SiteId);
            command.Parameters.AddWithValue("pt", parentType);
            command.Parameters.AddWithValue("pid", parentId);
            command.Parameters.AddWithValue("ct", childType);
            command.Parameters.AddWithValue("cid", childId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed record GenealogyRebuildReport(string SiteId, long FactsApplied, TimeSpan Elapsed);
