using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Contracts.Events.Traceability;
using Nvm.Kernel.EventSourcing;

namespace Nvm.Projections;

/// <summary>
/// Rebuild genealogy bằng một lượt quét feed: gom cạnh, gỡ liên kết và đoạn cuộn trong bộ nhớ theo thứ tự
/// feed, rồi ghi bằng binary COPY. Kết quả phải trùng với áp dụng từng event (kiểm bằng integration test).
/// </summary>
internal sealed class GenealogyBulkRebuild(string siteId)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<Link> _links = [];
    private readonly List<(string Roll, RollSegment Segment, DateTimeOffset RecordedAt, Guid EventId)> _segments = [];
    private readonly Dictionary<string, long> _progress = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Upstream, string Container), List<int>> _active = [];

    public long Facts { get; private set; }

    public void Add(StoredStreamEvent fact)
    {
        var applied = _progress.GetValueOrDefault(fact.StreamId);
        if (applied >= fact.Version)
        { return; }
        if (applied != fact.Version - 1 || fact.SchemaVersion != 1 || fact.SiteId != siteId)
        { throw new InvalidDataException("Genealogy stream version has a gap or belongs to another site."); }
        switch (fact.EventType)
        {
            case GenealogyProjection.Consumed:
                var consumed = Read<MaterialLotConsumed>(fact);
                AddLink(new Link(1, consumed.LotKind == "Roll" ? (short)5 : (short)1, consumed.LotId,
                    GenealogyProjection.UnitNodeType(consumed.ConsumerSerialNumber), consumed.ConsumerSerialNumber,
                    null, consumed.SpanFromMeter, consumed.SpanToMeter, consumed.Quantity, consumed.UnitOfMeasure,
                    consumed.OperationRunId, consumed.OccurredAt, consumed.RecordedAt, fact.SourceEventId));
                break;
            case GenealogyProjection.Assembled:
                var assembled = Read<UnitAssembledInto>(fact);
                AddLink(new Link(2, GenealogyProjection.UnitNodeType(assembled.ChildSerialNumber),
                    assembled.ChildSerialNumber, GenealogyProjection.UnitNodeType(assembled.ParentSerialNumber),
                    assembled.ParentSerialNumber, assembled.Position, null, null, null, null,
                    assembled.OperationRunId, assembled.OccurredAt, assembled.RecordedAt, fact.SourceEventId));
                break;
            case GenealogyProjection.Removed:
                var removed = Read<UnitRemovedFrom>(fact);
                Unlink(removed.ChildSerialNumber, removed.ParentSerialNumber, removed.OccurredAt, fact.SourceEventId, null);
                break;
            case GenealogyProjection.Corrected:
                var corrected = Read<GenealogyCorrectionRecorded>(fact);
                var replacement = AddLink(new Link(5, GenealogyProjection.UnitNodeType(corrected.ChildSerialNumber),
                    corrected.ChildSerialNumber, GenealogyProjection.UnitNodeType(corrected.CorrectParentSerialNumber),
                    corrected.CorrectParentSerialNumber, corrected.Position, null, null, null, null, "CORRECTION",
                    corrected.OccurredAt, corrected.RecordedAt, fact.SourceEventId));
                Unlink(corrected.ChildSerialNumber, corrected.WrongParentSerialNumber, corrected.OccurredAt,
                    fact.SourceEventId, replacement);
                break;
            case GenealogyProjection.Coated:
                var coated = Read<RollCoated>(fact);
                foreach (var segment in coated.Segments)
                { _segments.Add((coated.RollId, segment, coated.RecordedAt, fact.SourceEventId)); }
                break;
            default:
                throw new InvalidDataException($"Unsupported genealogy event: {fact.EventType}.");
        }
        _progress[fact.StreamId] = fact.Version;
        Facts++;
    }

    public async Task WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        long firstId;
        await using (var reserve = new NpgsqlCommand("""
            SELECT nextval(pg_get_serial_sequence('trace.genealogy_link', 'id'));
            """, connection, transaction))
        { firstId = (long)(await reserve.ExecuteScalarAsync(ct).ConfigureAwait(false))!; }
        await using (var stage = new NpgsqlCommand("""
            CREATE TEMP TABLE genealogy_stage (LIKE trace.genealogy_link INCLUDING DEFAULTS) ON COMMIT DROP;
            ALTER TABLE genealogy_stage ALTER COLUMN id DROP IDENTITY IF EXISTS;
            """, connection, transaction))
        { await stage.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY genealogy_stage (id, site_id, edge_kind, parent_type, parent_id, child_type, child_id, position, span,
                quantity, uom, operation_run_id, linked_at, recorded_at, unlinked_at, unlink_event_id, superseded_by,
                source_event_id) FROM STDIN (FORMAT BINARY)
            """, ct).ConfigureAwait(false))
        {
            for (var i = 0; i < _links.Count; i++)
            {
                var link = _links[i];
                await copy.StartRowAsync(ct).ConfigureAwait(false);
                await copy.WriteAsync(firstId + i, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                await copy.WriteAsync(siteId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(link.Kind, NpgsqlDbType.Smallint, ct).ConfigureAwait(false);
                await copy.WriteAsync(link.ParentType, NpgsqlDbType.Smallint, ct).ConfigureAwait(false);
                await copy.WriteAsync(link.ParentId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(link.ChildType, NpgsqlDbType.Smallint, ct).ConfigureAwait(false);
                await copy.WriteAsync(link.ChildId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await WriteNullableAsync(copy, link.Position, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                if (link.SpanFrom is { } from && link.SpanTo is { } to)
                { await copy.WriteAsync(new NpgsqlRange<decimal>(from, true, to, false), NpgsqlDbType.NumericRange, ct).ConfigureAwait(false); }
                else
                { await copy.WriteNullAsync(ct).ConfigureAwait(false); }
                await WriteNullableAsync(copy, link.Quantity, NpgsqlDbType.Numeric, ct).ConfigureAwait(false);
                await WriteNullableAsync(copy, link.Uom, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(link.OperationRunId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(link.LinkedAt, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                await copy.WriteAsync(link.RecordedAt, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                await WriteNullableAsync(copy, link.UnlinkedAt, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                await WriteNullableAsync(copy, link.UnlinkEventId, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
                await WriteNullableAsync(copy, link.SupersededBy is { } index ? firstId + index : (long?)null,
                    NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                await copy.WriteAsync(link.SourceEventId, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
            }
            await copy.CompleteAsync(ct).ConfigureAwait(false);
        }
        await using (var move = new NpgsqlCommand("""
            INSERT INTO trace.genealogy_link OVERRIDING SYSTEM VALUE SELECT * FROM genealogy_stage ORDER BY id;
            SELECT setval(pg_get_serial_sequence('trace.genealogy_link', 'id'),
                greatest((SELECT coalesce(max(id), 1) FROM trace.genealogy_link), @last));
            """, connection, transaction) { CommandTimeout = 0 })
        {
            move.Parameters.AddWithValue("last", firstId + Math.Max(0, _links.Count - 1));
            await move.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var copy = await connection.BeginBinaryImportAsync("""
            COPY trace.roll_segment (site_id, roll_id, web_side, span, slurry_batch_id, foil_lot_id, recipe_version_id,
                equipment_id, recorded_at, source_event_id) FROM STDIN (FORMAT BINARY)
            """, ct).ConfigureAwait(false))
        {
            foreach (var (roll, segment, recordedAt, eventId) in _segments)
            {
                await copy.StartRowAsync(ct).ConfigureAwait(false);
                await copy.WriteAsync(siteId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(roll, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(segment.WebSide, NpgsqlDbType.Char, ct).ConfigureAwait(false);
                await copy.WriteAsync(new NpgsqlRange<decimal>(segment.FromMeter, true, segment.ToMeter, false),
                    NpgsqlDbType.NumericRange, ct).ConfigureAwait(false);
                await copy.WriteAsync(segment.SlurryBatchId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(segment.FoilLotId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(segment.RecipeVersionId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(segment.EquipmentId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(recordedAt, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);
                await copy.WriteAsync(eventId, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
            }
            await copy.CompleteAsync(ct).ConfigureAwait(false);
        }
        await using (var copy = await connection.BeginBinaryImportAsync(
            "COPY trace.stream_progress (site_id, stream_id, stream_version) FROM STDIN (FORMAT BINARY)", ct)
            .ConfigureAwait(false))
        {
            foreach (var (stream, version) in _progress)
            {
                await copy.StartRowAsync(ct).ConfigureAwait(false);
                await copy.WriteAsync(siteId, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(stream, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                await copy.WriteAsync(version, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
            }
            await copy.CompleteAsync(ct).ConfigureAwait(false);
        }
    }

    private int AddLink(Link link)
    {
        _links.Add(link);
        var index = _links.Count - 1;
        if (link.Kind is 2 or 5)
        {
            var key = (link.ParentId, link.ChildId);
            if (!_active.TryGetValue(key, out var list))
            { _active[key] = list = []; }
            list.Add(index);
        }
        return index;
    }

    private void Unlink(string upstream, string container, DateTimeOffset at, Guid eventId, int? supersededBy)
    {
        if (!_active.TryGetValue((upstream, container), out var list) || list.Count == 0)
        { throw new InvalidDataException("No active genealogy link to remove."); }
        var index = list[^1];
        list.RemoveAt(list.Count - 1);
        _links[index] = _links[index] with { UnlinkedAt = at, UnlinkEventId = eventId, SupersededBy = supersededBy };
    }

    private static async Task WriteNullableAsync<T>(NpgsqlBinaryImporter copy, T? value, NpgsqlDbType type,
        CancellationToken ct) where T : struct
    {
        if (value is { } present)
        { await copy.WriteAsync(present, type, ct).ConfigureAwait(false); }
        else
        { await copy.WriteNullAsync(ct).ConfigureAwait(false); }
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter copy, string? value, NpgsqlDbType type,
        CancellationToken ct)
    {
        if (value is null)
        { await copy.WriteNullAsync(ct).ConfigureAwait(false); }
        else
        { await copy.WriteAsync(value, type, ct).ConfigureAwait(false); }
    }

    private static T Read<T>(StoredStreamEvent fact) where T : Nvm.Contracts.Events.IDomainEvent =>
        JsonSerializer.Deserialize<T>(fact.PayloadJson, Json) is { } value && value.EventId == fact.SourceEventId
            ? value : throw new InvalidDataException("Genealogy event payload is null or has another ID.");

    private sealed record Link(short Kind, short ParentType, string ParentId, short ChildType, string ChildId,
        string? Position, decimal? SpanFrom, decimal? SpanTo, decimal? Quantity, string? Uom, string OperationRunId,
        DateTimeOffset LinkedAt, DateTimeOffset RecordedAt, Guid SourceEventId)
    {
        public DateTimeOffset? UnlinkedAt { get; init; }
        public Guid? UnlinkEventId { get; init; }
        public int? SupersededBy { get; init; }
    }
}
