using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Contracts.Queries;
using Nvm.Traceability.Entities;
using Nvm.Traceability.Ports;

namespace Nvm.Traceability.Hosting;

/// <summary>All operations use the command claim's SQL connection and transaction.</summary>
public sealed class SqlTraceabilityAdapters(SqlCommandSession session, IUnitQualityFacet quality) :
    IRoutingDirectory, IUnitGuard, ISerialReservation, IDuplicateSerialQuarantine
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<UnitRouting?> FindAsync(string siteId, string productCode, string routingVersion,
        CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        using var command = session.CreateCommand("""
            SELECT StepsJson, TransitionsJson FROM traceability.Routes WITH (HOLDLOCK)
            WHERE SiteId = @site AND ProductCode = @product AND RoutingVersion = @version;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@product", SqlDbType.NVarChar, 100).Value = productCode;
        command.Parameters.Add("@version", SqlDbType.NVarChar, 50).Value = routingVersion;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        var stepsJson = reader.GetString(0);
        var transitionsJson = reader.GetString(1);
        var steps = JsonSerializer.Deserialize<RoutingStep[]>(stepsJson, Json)
            ?? throw new InvalidDataException("Stored routing steps are null.");
        var transitions = JsonSerializer.Deserialize<TransitionRule[]>(transitionsJson, Json)
            ?? throw new InvalidDataException("Stored routing transitions are null.");
        if (steps.Length == 0 || transitions.Length == 0 ||
            steps.Any(step => string.IsNullOrWhiteSpace(step.Code)) ||
            transitions.Any(rule => string.IsNullOrWhiteSpace(rule.Action)))
        { throw new InvalidDataException("Stored routing is empty or invalid."); }
        return new UnitRouting(siteId, productCode, routingVersion,
            steps.ToImmutableArray(), transitions.ToImmutableArray());
    }

    public async Task<UnitGuardSnapshot> ReadAsync(string siteId, string serialNumber, string actorId,
        CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        using var command = session.CreateCommand("""
            SELECT LocationState FROM traceability.SerialReservations WITH (HOLDLOCK)
            WHERE SiteId = @site AND SerialNumber = @serial;
            """);
        AddIdentity(command, siteId, serialNumber);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { throw new InvalidDataException("Serialized unit has no serial reservation."); }
        var location = Enum.Parse<LocationState>(reader.GetString(0), ignoreCase: false);
        await reader.DisposeAsync().ConfigureAwait(false);
        var facet = await quality.ReadForCommandAsync(siteId, serialNumber, cancellationToken).ConfigureAwait(false);

        using var rolesCommand = session.CreateCommand("""
            SELECT RoleCode FROM traceability.ActorRoles WITH (HOLDLOCK)
            WHERE SiteId = @site AND ActorId = @actor;
            """);
        rolesCommand.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        rolesCommand.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        using var rolesReader = await rolesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var roles = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        while (await rolesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { roles.Add(rolesReader.GetString(0)); }
        return new UnitGuardSnapshot(facet, location, roles.ToImmutable());
    }

    public async Task<SerialReservationOutcome> ReserveAsync(string siteId, string serialNumber,
        Guid eventId, CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        if (eventId == Guid.Empty)
        { throw new ArgumentException("Event ID is required.", nameof(eventId)); }
        // A serializable key-range lock makes the empty-key check safe across command sessions.
        using var command = session.CreateCommand("""
            SELECT 1 FROM traceability.SerialReservations WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND SerialNumber = @serial;
            """);
        AddIdentity(command, siteId, serialNumber);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
        { return SerialReservationOutcome.Duplicate; }
        using var insert = session.CreateCommand("""
            INSERT INTO traceability.SerialReservations (SiteId, SerialNumber, SerializedEventId)
            VALUES (@site, @serial, @event);
            """);
        AddIdentity(insert, siteId, serialNumber);
        insert.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = eventId;
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return SerialReservationOutcome.Reserved;
    }

    public async Task RecordAsync(string siteId, string serialNumber, string submissionId, Guid eventId,
        string actorId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        RequireSite(siteId);
        using var incident = session.CreateCommand("""
            INSERT INTO traceability.DuplicateSerialIncidents
                (SiteId, SerialNumber, SubmissionId, EventId, ActorId, OccurredAt)
            VALUES (@site, @serial, @submission, @event, @actor, @occurred);
            """);
        AddIdentity(incident, siteId, serialNumber);
        incident.Parameters.Add("@submission", SqlDbType.NVarChar, 200).Value = submissionId;
        incident.Parameters.Add("@event", SqlDbType.UniqueIdentifier).Value = eventId;
        incident.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        incident.Parameters.Add("@occurred", SqlDbType.DateTimeOffset).Value = occurredAt;
        await incident.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RequireSite(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Traceability site does not match the active command transaction."); }
    }

    private static void AddIdentity(SqlCommand command, string siteId, string serialNumber)
    {
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = serialNumber;
    }
}
