using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Traceability;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;

namespace Nvm.Projections;

/// <summary>Durable PostgreSQL projection of the current state of serialized units.</summary>
public sealed class ProductionUnitProjection(NpgsqlDataSource dataSource, IGlobalEventFeed feed)
{
    public const string Name = "unit-current-v1";
    private const string Serialized = "com.novavolt.traceability.unit-serialized.v1";
    private const string Started = "com.novavolt.traceability.process-step-started.v1";
    private const string Completed = "com.novavolt.traceability.process-step-completed.v1";
    private const string Measurement = "com.novavolt.traceability.unit-measurement-recorded.v1";
    private const string DuplicateSerial = "com.novavolt.traceability.duplicate-serial-detected.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reconciliation/rebuild only: each pass scans the full site feed. Use the durable inbox for live delivery. SQL Server identities do not prove commit order across
    /// transactions, so a high-water checkpoint alone could permanently skip a late commit.
    /// Unit stream versions make the repeated scan idempotent. The caller chooses polling cadence.
    /// </summary>
    public async Task<long> CatchUpAsync(string siteId, int batchSize = 500, CancellationToken cancellationToken = default)
    {
        ProjectionIdentity.ValidateSite(siteId);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        long cursor = 0;
        long? generation = null;
        while (true)
        {
            var batch = await feed.ReadAsync(siteId, cursor, batchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            { break; }
            generation = await ApplyBatchAsync(siteId, batch, generation, cancellationToken).ConfigureAwait(false);
            cursor = batch[^1].GlobalSequence;
            if (batch.Count < batchSize)
            { break; }
        }
        return cursor;
    }

    /// <summary>Applies facts and checkpoint in one PostgreSQL transaction.</summary>
    public async Task<long> ApplyBatchAsync(string siteId, IReadOnlyList<StoredStreamEvent> events,
        long? expectedGeneration = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var generation = await ApplyBatchInTransactionAsync(connection, transaction, siteId, events,
            expectedGeneration, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return generation;
    }

    internal static async Task<long> ApplyBatchInTransactionAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string siteId, IReadOnlyList<StoredStreamEvent> events,
        long? expectedGeneration, CancellationToken cancellationToken)
    {
        ProjectionIdentity.ValidateSite(siteId);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        { throw new ArgumentException("Batch cannot be empty.", nameof(events)); }
        long previous = 0;
        foreach (var fact in events)
        {
            if (fact.SiteId != siteId || fact.GlobalSequence <= previous)
            { throw new InvalidDataException("Batch must contain strictly ordered facts for one site."); }
            previous = fact.GlobalSequence;
        }

        var generation = await LockCheckpointAsync(connection, transaction, siteId, cancellationToken).ConfigureAwait(false);
        if (expectedGeneration is not null && expectedGeneration != generation)
        { throw new InvalidOperationException("Projection was rebuilt during catch-up; restart the scan."); }

        foreach (var fact in events)
        {
            await ApplyAsync(connection, transaction, fact, cancellationToken).ConfigureAwait(false);
        }

        await using (var checkpoint = new NpgsqlCommand("""
            UPDATE rm.projection_checkpoint
            SET last_global_seq = GREATEST(last_global_seq, @last)
            WHERE site_id = @site AND projection_name = @name;
            """, connection, transaction))
        {
            checkpoint.Parameters.AddWithValue("last", previous);
            checkpoint.Parameters.AddWithValue("site", siteId);
            checkpoint.Parameters.AddWithValue("name", Name);
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return generation;
    }

    /// <summary>Resets one site's read model atomically; the next catch-up reconstructs it.</summary>
    public async Task RebuildAsync(string siteId, CancellationToken cancellationToken = default)
    {
        ProjectionIdentity.ValidateSite(siteId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockCheckpointAsync(connection, transaction, siteId, cancellationToken).ConfigureAwait(false);
        await using (var clear = new NpgsqlCommand(
            "DELETE FROM rm.unit_current WHERE site_id = @site;", connection, transaction))
        {
            clear.Parameters.AddWithValue("site", siteId);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var reset = new NpgsqlCommand("""
            UPDATE rm.projection_checkpoint
            SET last_global_seq = 0, generation = generation + 1
            WHERE site_id = @site AND projection_name = @name;
            """, connection, transaction))
        {
            reset.Parameters.AddWithValue("site", siteId);
            reset.Parameters.AddWithValue("name", Name);
            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<long> LockCheckpointAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string siteId, CancellationToken cancellationToken)
    {
        await using (var ensure = new NpgsqlCommand("""
            INSERT INTO rm.projection_checkpoint(site_id, projection_name)
            VALUES (@site, @name) ON CONFLICT DO NOTHING;
            """, connection, transaction))
        {
            ensure.Parameters.AddWithValue("site", siteId);
            ensure.Parameters.AddWithValue("name", Name);
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var command = new NpgsqlCommand("""
            SELECT generation FROM rm.projection_checkpoint
            WHERE site_id = @site AND projection_name = @name FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("site", siteId);
        command.Parameters.AddWithValue("name", Name);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Projection checkpoint is missing."));
    }

    private static async Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        StoredStreamEvent fact, CancellationToken cancellationToken)
    {
        switch (fact.EventType)
        {
            case Serialized:
                {
                    var born = Read<ProductionUnitSerialized>(fact);
                    EnsureIdentity(fact, born);
                    var serial = SerialNumber.Parse(born.SerialNumber);
                    if (serial.SiteCode != fact.SiteId || born.UnitKind != serial.Kind.ToString())
                    { throw new InvalidDataException("Serialized unit kind or site differs from its serial."); }
                    if (fact.Version != 1)
                    { throw new InvalidDataException("Serialization must be stream version one."); }
                    await using var insert = new NpgsqlCommand("""
                    INSERT INTO rm.unit_current(site_id, serial_number, unit_kind, product_code,
                        work_order_id, routing_version, execution_state, stream_version, last_global_seq)
                    VALUES (@site, @serial, @kind, @product, @work, @routing, 'Scheduled', 1, @seq)
                    ON CONFLICT (site_id, serial_number) DO NOTHING;
                    """, connection, transaction);
                    insert.Parameters.AddWithValue("site", fact.SiteId);
                    insert.Parameters.AddWithValue("serial", fact.StreamId);
                    insert.Parameters.AddWithValue("kind", born.UnitKind);
                    insert.Parameters.AddWithValue("product", born.ProductCode);
                    insert.Parameters.AddWithValue("work", born.WorkOrderId);
                    insert.Parameters.AddWithValue("routing", born.RoutingVersion);
                    insert.Parameters.AddWithValue("seq", fact.GlobalSequence);
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }
            case Started:
                {
                    var started = Read<ProcessStepStarted>(fact);
                    EnsureIdentity(fact, started);
                    var state = await ReadStateAsync(connection, transaction, fact, cancellationToken).ConfigureAwait(false);
                    if (state.Version >= fact.Version)
                    { break; }
                    EnsureNext(state, fact);
                    await UpdateAsync(connection, transaction, fact, started.StepCode, started.OperationRunId,
                        started.EquipmentPath, "Running", state.CompletedSteps, cancellationToken).ConfigureAwait(false);
                    break;
                }
            case Completed:
                {
                    var done = Read<ProcessStepCompleted>(fact);
                    EnsureIdentity(fact, done);
                    var state = await ReadStateAsync(connection, transaction, fact, cancellationToken).ConfigureAwait(false);
                    if (state.Version >= fact.Version)
                    { break; }
                    EnsureNext(state, fact);
                    if (state.Execution != "Running" || state.Step != done.StepCode || state.Run != done.OperationRunId)
                    { throw new InvalidDataException("Completion does not match the running step."); }
                    var steps = state.CompletedSteps.Append(done.StepCode).Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal).ToArray();
                    await UpdateAsync(connection, transaction, fact, state.Step, state.Run, state.Equipment,
                        "Completed", steps, cancellationToken).ConfigureAwait(false);
                    break;
                }
            case Measurement:
                {
                    var measured = Read<UnitMeasurementRecorded>(fact);
                    EnsureIdentity(fact, measured);
                    var state = await ReadStateAsync(connection, transaction, fact, cancellationToken).ConfigureAwait(false);
                    if (state.Version >= fact.Version)
                    { break; }
                    EnsureNext(state, fact);
                    await UpdateAsync(connection, transaction, fact, state.Step, state.Run, state.Equipment,
                        state.Execution, state.CompletedSteps, cancellationToken).ConfigureAwait(false);
                    break;
                }
            case DuplicateSerial:
                break;
            default:
                if (fact.EventType.StartsWith("com.novavolt.traceability.", StringComparison.Ordinal))
                { throw new InvalidDataException($"Unsupported traceability event: {fact.EventType}."); }
                break;
        }
    }

    private static T Read<T>(StoredStreamEvent fact) where T : IDomainEvent
    {
        if (fact.SchemaVersion != 1)
        { throw new InvalidDataException("Unsupported event schema version."); }
        var value = JsonSerializer.Deserialize<T>(fact.PayloadJson, Json)
            ?? throw new InvalidDataException("Event payload is null.");
        if (value.EventId != fact.SourceEventId)
        { throw new InvalidDataException("Event ID differs from stored source event ID."); }
        return value;
    }

    private static void EnsureIdentity(StoredStreamEvent fact, IDomainEvent payload)
    {
        var identity = payload switch
        {
            ProductionUnitSerialized e => (e.SiteId, e.SerialNumber),
            ProcessStepStarted e => (e.SiteId, e.SerialNumber),
            ProcessStepCompleted e => (e.SiteId, e.SerialNumber),
            UnitMeasurementRecorded e => (e.SiteId, e.SerialNumber),
            _ => throw new InvalidDataException("Unsupported unit event.")
        };
        if (identity.SiteId != fact.SiteId || identity.SerialNumber != fact.StreamId)
        { throw new InvalidDataException("Event payload belongs to another site or stream."); }
    }

    private static async Task<UnitState> ReadStateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, StoredStreamEvent fact, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT stream_version, current_step, operation_run_id, equipment_path,
                execution_state, completed_steps::text
            FROM rm.unit_current WHERE site_id = @site AND serial_number = @serial FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("site", fact.SiteId);
        command.Parameters.AddWithValue("serial", fact.StreamId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { throw new InvalidDataException("Unit event precedes serialization."); }
        return new UnitState(reader.GetInt64(0),
            await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
            reader.GetString(4),
            JsonSerializer.Deserialize<string[]>(reader.GetString(5), Json)
                ?? throw new InvalidDataException("Invalid completed steps."));
    }

    private static void EnsureNext(UnitState state, StoredStreamEvent fact)
    {
        if (state.Version != fact.Version - 1)
        { throw new InvalidDataException("Unit stream version has a gap."); }
    }

    private static async Task UpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        StoredStreamEvent fact, string? step, string? run, string? equipment, string execution,
        string[] completedSteps, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE rm.unit_current SET current_step = @step, operation_run_id = @run,
                equipment_path = @equipment, execution_state = @execution,
                completed_steps = @completed, stream_version = @version,
                last_global_seq = @seq
            WHERE site_id = @site AND serial_number = @serial AND stream_version = @previous;
            """, connection, transaction);
        command.Parameters.AddWithValue("step", (object?)step ?? DBNull.Value);
        command.Parameters.AddWithValue("run", (object?)run ?? DBNull.Value);
        command.Parameters.AddWithValue("equipment", (object?)equipment ?? DBNull.Value);
        command.Parameters.AddWithValue("execution", execution);
        command.Parameters.Add("completed", NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(completedSteps, Json);
        command.Parameters.AddWithValue("version", fact.Version);
        command.Parameters.AddWithValue("seq", fact.GlobalSequence);
        command.Parameters.AddWithValue("site", fact.SiteId);
        command.Parameters.AddWithValue("serial", fact.StreamId);
        command.Parameters.AddWithValue("previous", fact.Version - 1);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidDataException("Unit state changed unexpectedly."); }
    }

    private sealed record UnitState(long Version, string? Step, string? Run, string? Equipment,
        string Execution, string[] CompletedSteps);
}
