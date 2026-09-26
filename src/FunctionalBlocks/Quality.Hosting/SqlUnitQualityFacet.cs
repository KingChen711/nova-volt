using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Quality;
using Nvm.Contracts.Queries;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;
using Nvm.Quality.Entities;
using Nvm.Quality.Facets;
using Nvm.Quality.Ports;

namespace Nvm.Quality.Hosting;

/// <summary>
/// Facet QualityState trên SQL, dùng connection/transaction của command đang giữ claim. Trạng thái hiệu lực gộp
/// quyết định riêng của unit (<c>quality.UnitQuality</c>) với mọi hold còn hiệu lực chứa unit (<c>quality.HoldMembers</c>).
/// </summary>
public sealed class SqlUnitQualityFacet(SqlCommandSession session, IEventStore events, TimeProvider clock)
    : IUnitQualityFacet, IUnitQualityWriter
{
    public const string StreamType = "unit-quality";

    public static string StreamId(string serialNumber) => "quality:" + serialNumber;

    public async Task<UnitQualityFacet> ReadForCommandAsync(string siteId, string serialNumber,
        CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        var current = await ReadAsync(siteId, serialNumber, lockForUpdate: false, cancellationToken)
            .ConfigureAwait(false);
        var own = current?.State ?? QualityState.Pending;
        // Hold theo lot/cuộn không ghi đè Scrapped: loại bỏ là quyết định cuối.
        if (own != QualityState.Scrapped && await HeldByActiveHoldAsync(siteId, serialNumber, cancellationToken).ConfigureAwait(false))
        { return UnitQualityGate.Describe(QualityState.Held); }
        return current is null ? UnitQualityFacet.Pending : UnitQualityGate.Describe(own);
    }

    public async Task QuarantineForIncidentAsync(string siteId, string serialNumber, Guid incidentEventId,
        string reasonCode, string actorId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (incidentEventId == Guid.Empty)
        { throw new ArgumentException("Incident event ID is required.", nameof(incidentEventId)); }
        await ChangeAsync(siteId, serialNumber, QualityState.Held, reasonCode, incidentEventId, actorId, occurredAt,
            cancellationToken).ConfigureAwait(false);
    }

    public Task SetStateAsync(string siteId, string serialNumber, QualityState state, string reasonCode, Guid causeEventId,
        string actorId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        return ChangeAsync(siteId, serialNumber, state, reasonCode, causeEventId, actorId, occurredAt, cancellationToken);
    }

    private async Task ChangeAsync(string siteId, string serialNumber, QualityState state, string reasonCode,
        Guid causeEventId, string actorId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(siteId, serialNumber, lockForUpdate: true, cancellationToken).ConfigureAwait(false);
        if (current is not null && !UnitQualityGate.CanQuarantine(current.Value.State))
        { return; }   // Scrapped là trạng thái cuối.

        var now = clock.GetUtcNow();
        // Mỗi nguyên nhân sinh đúng một event; replay cùng nguyên nhân cho ra cùng event ID.
        var eventId = IdempotencyKey.FromNaturalKey(siteId, "UnitQuality:" + state, causeEventId.ToString(), serialNumber).Value;
        IDomainEvent fact = state switch
        {
            QualityState.Held => new UnitQuarantined(eventId, occurredAt, now, siteId, serialNumber, reasonCode,
                causeEventId, actorId),
            QualityState.Scrapped => new UnitScrapped(eventId, occurredAt, now, siteId, serialNumber, reasonCode,
                causeEventId, actorId),
            QualityState.Released or QualityState.Rework => new UnitReleasedFromQuarantine(eventId, occurredAt, now,
                siteId, serialNumber, state.ToString(), reasonCode, causeEventId, actorId),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Pending is not a decision.")
        };
        var next = await events.AppendAsync(siteId, StreamId(serialNumber), StreamType, current?.Version ?? 0,
            ImmutableArray.Create(DomainEventRecord.Create(fact, UnitSubject(serialNumber), $"{siteId}:{serialNumber}", now,
                causationId: causeEventId)), cancellationToken).ConfigureAwait(false);

        using var upsert = session.CreateCommand(current is null
            ? """
              INSERT INTO quality.UnitQuality
                  (SiteId, SerialNumber, QualityState, ReasonCode, StreamVersion, LastEventId, UpdatedAt)
              VALUES (@site, @serial, @state, @reason, @version, @event, @at);
              """
            : """
              UPDATE quality.UnitQuality SET QualityState = @state, ReasonCode = @reason,
                  StreamVersion = @version, LastEventId = @event, UpdatedAt = @at
              WHERE SiteId = @site AND SerialNumber = @serial;
              """);
        AddIdentity(upsert, siteId, serialNumber);
        upsert.Parameters.Add("@state", SqlDbType.VarChar, 20).Value = state.ToString();
        upsert.Parameters.Add("@reason", SqlDbType.VarChar, 64).Value = reasonCode;
        upsert.Parameters.Add("@version", SqlDbType.BigInt).Value = next;
        upsert.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = eventId;
        upsert.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = now;
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HeldByActiveHoldAsync(string siteId, string serialNumber, CancellationToken cancellationToken)
    {
        using var command = session.CreateCommand("""
            SELECT TOP (1) 1 FROM quality.HoldMembers m WITH (HOLDLOCK)
            JOIN quality.Holds h WITH (HOLDLOCK) ON h.SiteId = m.SiteId AND h.HoldId = m.HoldId
            WHERE m.SiteId = @site AND m.SerialNumber = @serial AND h.Status = 'Active';
            """);
        AddIdentity(command, siteId, serialNumber);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<(QualityState State, long Version)?> ReadAsync(string siteId, string serialNumber,
        bool lockForUpdate, CancellationToken cancellationToken)
    {
        // HOLDLOCK giữ cả key chưa có dòng, để guard của command không lệch với một hold đang ghi.
        using var command = session.CreateCommand(lockForUpdate
            ? """
              SELECT QualityState, StreamVersion FROM quality.UnitQuality WITH (UPDLOCK, HOLDLOCK)
              WHERE SiteId = @site AND SerialNumber = @serial;
              """
            : """
              SELECT QualityState, StreamVersion FROM quality.UnitQuality WITH (HOLDLOCK)
              WHERE SiteId = @site AND SerialNumber = @serial;
              """);
        AddIdentity(command, siteId, serialNumber);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return (Enum.Parse<QualityState>(reader.GetString(0), ignoreCase: false), reader.GetInt64(1));
    }

    private static string UnitSubject(string serialNumber) =>
        $"urn:trace-unit:{SerialNumber.Parse(serialNumber).Kind.ToString().ToLowerInvariant()}:{serialNumber}";

    private void RequireSite(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Quality site does not match the active command transaction."); }
    }

    private static void AddIdentity(SqlCommand command, string siteId, string serialNumber)
    {
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
    }
}
