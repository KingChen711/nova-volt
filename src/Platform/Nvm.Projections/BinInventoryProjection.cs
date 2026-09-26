using System.Text.Json;
using MassTransit;
using Npgsql;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Grading;
using Nvm.Contracts.Events.Traceability;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;

namespace Nvm.Projections;

/// <summary>Read model <c>rm.bin_inventory</c>: bin hiện tại của cell và cell đã nằm trong module hay chưa.</summary>
public sealed class BinInventoryProjection : IOrderedProjection
{
    private const string Graded = "com.novavolt.grading.unit-graded.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "bin-inventory-v1";

    public bool Accepts(string eventType) => eventType is Graded or GenealogyProjection.Assembled
        or GenealogyProjection.Removed or GenealogyProjection.Corrected;

    public async Task ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StoredStreamEvent fact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fact);
        switch (fact.EventType)
        {
            case Graded:
                var graded = Read<UnitGraded>(fact);
                Ensure(fact, graded.SiteId, graded.SerialNumber);
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO rm.bin_inventory (site_id, serial_number, product_code, bin_code, reject_code, capacity_ah,
                        ocv_mv, dcir_mohm, graded_at, evaluation_id, rule_set)
                    VALUES (@site, @serial, @product, @bin, @reject, @capacity, @ocv, @dcir, @at, @evaluation, @rule)
                    ON CONFLICT (site_id, serial_number) DO UPDATE SET product_code = excluded.product_code,
                        bin_code = excluded.bin_code, reject_code = excluded.reject_code, capacity_ah = excluded.capacity_ah,
                        ocv_mv = excluded.ocv_mv, dcir_mohm = excluded.dcir_mohm, graded_at = excluded.graded_at,
                        evaluation_id = excluded.evaluation_id, rule_set = excluded.rule_set;
                    """, command =>
                {
                    command.Parameters.AddWithValue("site", fact.SiteId);
                    command.Parameters.AddWithValue("serial", graded.SerialNumber);
                    command.Parameters.AddWithValue("product", graded.ProductCode);
                    command.Parameters.AddWithValue("bin", (object?)graded.BinCode ?? DBNull.Value);
                    command.Parameters.AddWithValue("reject", (object?)graded.RejectCode ?? DBNull.Value);
                    command.Parameters.AddWithValue("capacity", graded.CapacityAh);
                    command.Parameters.AddWithValue("ocv", graded.OcvMillivolt);
                    command.Parameters.AddWithValue("dcir", graded.DcirMilliOhm);
                    command.Parameters.AddWithValue("at", graded.OccurredAt);
                    command.Parameters.AddWithValue("evaluation", graded.EventId);
                    command.Parameters.AddWithValue("rule", $"{graded.RuleSetId}:v{graded.RuleSetVersion}");
                }, cancellationToken).ConfigureAwait(false);
                break;
            case GenealogyProjection.Assembled:
                var assembled = Read<UnitAssembledInto>(fact);
                await MembershipAsync(connection, transaction, fact, assembled.SiteId, assembled.ChildSerialNumber,
                    assembled.ParentSerialNumber, cancellationToken).ConfigureAwait(false);
                break;
            case GenealogyProjection.Removed:
                var removed = Read<UnitRemovedFrom>(fact);
                await MembershipAsync(connection, transaction, fact, removed.SiteId, removed.ChildSerialNumber, null,
                    cancellationToken).ConfigureAwait(false);
                break;
            case GenealogyProjection.Corrected:
                var corrected = Read<GenealogyCorrectionRecorded>(fact);
                await MembershipAsync(connection, transaction, fact, corrected.SiteId, corrected.ChildSerialNumber,
                    corrected.CorrectParentSerialNumber, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException($"Unsupported bin inventory event: {fact.EventType}.");
        }
    }

    public async Task ResetAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string siteId,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction, "DELETE FROM rm.bin_inventory WHERE site_id = @site;",
            command => command.Parameters.AddWithValue("site", siteId), cancellationToken).ConfigureAwait(false);

    private static async Task MembershipAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        StoredStreamEvent fact, string siteId, string child, string? parent, CancellationToken cancellationToken)
    {
        Ensure(fact, siteId, child);
        if (SerialNumber.Parse(child).Kind != ProductionUnitKind.Cell)
        { return; }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO rm.bin_inventory (site_id, serial_number, assembled_into) VALUES (@site, @serial, @parent)
            ON CONFLICT (site_id, serial_number) DO UPDATE SET assembled_into = excluded.assembled_into;
            """, command =>
        {
            command.Parameters.AddWithValue("site", siteId);
            command.Parameters.AddWithValue("serial", child);
            command.Parameters.AddWithValue("parent", (object?)parent ?? DBNull.Value);
        }, cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "SQL là hằng trong class này.")]
    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static T Read<T>(StoredStreamEvent fact) where T : IDomainEvent =>
        JsonSerializer.Deserialize<T>(fact.PayloadJson, Json) is { } value && value.EventId == fact.SourceEventId
            ? value : throw new InvalidDataException("Bin inventory event payload is null or has another ID.");

    private static void Ensure(StoredStreamEvent fact, string siteId, string serial)
    {
        if (siteId != fact.SiteId || !fact.StreamId.EndsWith(":" + serial, StringComparison.Ordinal))
        { throw new InvalidDataException("Bin inventory event belongs to another site or stream."); }
    }
}

/// <summary>Đưa event grading và lắp ráp vào inbox dùng chung của các projection có thứ tự.</summary>
[BusEndpoint("grading", "ordered-projections")]
public sealed class OrderedProjectionsConsumer(SqlGlobalEventFeed source, OrderedProjectionRunner runner)
    : OrderedProjectionConsumer(source, runner), IConsumer<UnitGraded>, IConsumer<UnitAssembledInto>,
        IConsumer<UnitRemovedFrom>, IConsumer<GenealogyCorrectionRecorded>
{
    public Task Consume(ConsumeContext<UnitGraded> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<UnitAssembledInto> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<UnitRemovedFrom> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<GenealogyCorrectionRecorded> context) => CaptureAsync(context);
}
