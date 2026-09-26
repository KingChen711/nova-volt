using System.Collections.Immutable;
using System.Data;
using Nvm.CommandStore;
using Nvm.Contracts.Events.Quality;
using Nvm.Contracts.Ports;
using Nvm.Contracts.Queries;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;

namespace Nvm.Quality.Hosting;

/// <summary>Giữ unit và mở NCR trong transaction của command gọi; idempotent theo fact nguồn.</summary>
public sealed class SqlQualityIncidents(SqlCommandSession session, IEventStore events, IUnitQualityFacet facet,
    TimeProvider clock) : IQualityIncidents
{
    public const string StreamType = "non-conformance";

    public async Task<QualityIncident> QuarantineWithNonConformanceAsync(string siteId, string serialNumber,
        string reasonCode, string description, string source, Guid sourceEventId, string actorId,
        DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Quality site does not match the active command transaction."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var ncrId = "NCR-" + sourceEventId.ToString("N").ToUpperInvariant();
        using (var existing = session.CreateCommand("""
            SELECT 1 FROM quality.NonConformance WITH (UPDLOCK, HOLDLOCK) WHERE SiteId = @site AND NcrId = @ncr;
            """))
        {
            existing.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
            existing.Parameters.Add("@ncr", SqlDbType.VarChar, 64).Value = ncrId;
            if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return new QualityIncident(ncrId,
                    IdempotencyKey.FromNaturalKey(siteId, "NonConformanceRaised", sourceEventId.ToString()).Value);
            }
        }

        await facet.QuarantineForIncidentAsync(siteId, serialNumber, sourceEventId, reasonCode, actorId, occurredAt,
            cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var eventId = IdempotencyKey.FromNaturalKey(siteId, "NonConformanceRaised", sourceEventId.ToString()).Value;
        var fact = new NonConformanceRaised(eventId, occurredAt, now, siteId, ncrId, serialNumber, reasonCode,
            description, source, sourceEventId, actorId);
        var version = await events.AppendAsync(siteId, "ncr:" + ncrId, StreamType, 0,
            ImmutableArray.Create(DomainEventRecord.Create(fact, "urn:ncr:" + ncrId, $"{siteId}:{ncrId}", now,
                causationId: sourceEventId)), cancellationToken).ConfigureAwait(false);
        using var insert = session.CreateCommand("""
            INSERT INTO quality.NonConformance (SiteId, NcrId, SerialNumber, ReasonCode, Description, Source,
                SourceEventId, Status, StreamVersion, RaisedAt, RaisedBy)
            VALUES (@site, @ncr, @serial, @reason, @description, @source, @sourceEvent, 'Open', @version, @at, @actor);
            """);
        insert.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        insert.Parameters.Add("@ncr", SqlDbType.VarChar, 64).Value = ncrId;
        insert.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
        insert.Parameters.Add("@reason", SqlDbType.VarChar, 64).Value = reasonCode;
        insert.Parameters.Add("@description", SqlDbType.NVarChar, 1000).Value = description;
        insert.Parameters.Add("@source", SqlDbType.VarChar, 50).Value = source;
        insert.Parameters.Add("@sourceEvent", SqlDbType.UniqueIdentifier).Value = sourceEventId;
        insert.Parameters.Add("@version", SqlDbType.BigInt).Value = version;
        insert.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = now;
        insert.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new QualityIncident(ncrId, eventId);
    }
}
