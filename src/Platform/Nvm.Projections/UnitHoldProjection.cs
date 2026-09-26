using System.Text.Json;
using Npgsql;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Quality;
using Nvm.Kernel.EventSourcing;

namespace Nvm.Projections;

/// <summary>
/// Hold theo lot/cuộn trên read model: trạng thái hold (<c>rm.hold_status</c>) và unit thuộc hold
/// (<c>rm.unit_hold</c>). Hai stream nguồn khác nhau; unit chỉ hiện Held khi hold đã biết và còn hiệu lực.
/// </summary>
public sealed class UnitHoldProjection : IOrderedProjection
{
    private const string Placed = "com.novavolt.quality.quality-hold-placed.v1";
    private const string Chunk = "com.novavolt.quality.units-held-by-cascade.v1";
    private const string Released = "com.novavolt.quality.quality-hold-released.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "unit-hold-v1";

    public bool Accepts(string eventType) => eventType is Placed or Chunk or Released;

    public async Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StoredStreamEvent fact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        switch (fact.EventType)
        {
            case Placed:
                var placed = Read<QualityHoldPlaced>(fact);
                await StatusAsync(connection, transaction, fact.SiteId, placed.HoldId, true, cancellationToken).ConfigureAwait(false);
                break;
            case Released:
                var released = Read<QualityHoldReleased>(fact);
                await StatusAsync(connection, transaction, fact.SiteId, released.HoldId, false, cancellationToken).ConfigureAwait(false);
                break;
            case Chunk:
                var chunk = Read<UnitsHeldByCascade>(fact);
                await using (var insert = new NpgsqlCommand("""
                    INSERT INTO rm.unit_hold (site_id, serial_number, hold_id)
                    SELECT @site, serial, @hold FROM unnest(@serials) AS serial ON CONFLICT DO NOTHING;
                    """, connection, transaction))
                {
                    insert.Parameters.AddWithValue("site", fact.SiteId);
                    insert.Parameters.AddWithValue("hold", chunk.HoldId);
                    insert.Parameters.AddWithValue("serials", chunk.SerialNumbers.ToArray());
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                break;
            default:
                throw new InvalidDataException($"Unsupported hold event: {fact.EventType}.");
        }
    }

    public async Task ResetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "DELETE FROM rm.unit_hold WHERE site_id = @site; DELETE FROM rm.hold_status WHERE site_id = @site;",
            connection, transaction);
        command.Parameters.AddWithValue("site", siteId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task StatusAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteId,
        string holdId, bool active, CancellationToken cancellationToken)
    {
        // Released không bao giờ quay lại Active, kể cả khi fact Placed được áp dụng sau.
        await using var command = new NpgsqlCommand("""
            INSERT INTO rm.hold_status (site_id, hold_id, active) VALUES (@site, @hold, @active)
            ON CONFLICT (site_id, hold_id) DO UPDATE SET active = rm.hold_status.active AND excluded.active;
            """, connection, transaction);
        command.Parameters.AddWithValue("site", siteId);
        command.Parameters.AddWithValue("hold", holdId);
        command.Parameters.AddWithValue("active", active);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static T Read<T>(StoredStreamEvent fact) where T : IDomainEvent =>
        JsonSerializer.Deserialize<T>(fact.PayloadJson, Json) is { } value && value.EventId == fact.SourceEventId &&
        value.SiteId == fact.SiteId
            ? value : throw new InvalidDataException("Hold event payload is null, from another site or has another ID.");
}
