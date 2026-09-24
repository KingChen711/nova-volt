using System.Data;
using Nvm.CommandStore;
using Nvm.ProductionExecution.Ports;

namespace Nvm.ProductionExecution.Hosting;

/// <summary>Adapter INSERT kết quả đo trong CHÍNH transaction của command (cùng scoped session).</summary>
/// <remarks>
/// Không mở connection thứ hai: nếu để DI cấp một session/transaction riêng thì effect nằm ngoài claim.
/// Bảng append-only, runtime chỉ có quyền INSERT/SELECT (migration 002 DENY UPDATE/DELETE).
/// </remarks>
public sealed class SqlDataCollectionStore(SqlCommandSession session, SqlCommandStoreOptions options)
    : IDataCollectionStore
{
    private readonly SqlCommandSession _session = session;
    private readonly SqlCommandStoreOptions _options = options;

    /// <inheritdoc />
    public async Task AppendAsync(DataCollectionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Site của record phải bằng site đã giữ transaction; lệch nhau là lỗi wiring, không im lặng ghi.
        if (!string.Equals(record.SiteId, _session.SiteId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Data collection site does not match the claimed transaction.");
        }

        using var command = _session.CreateCommand("""
            INSERT INTO execution.DataCollection
                (SiteId, IdempotencyKey, SubmissionId, SerialNumber, OperationRunId, StepCode,
                 EquipmentPath, SignalCode, Value, UnitOfMeasure, ActorId, OccurredAt, RecordedAt, PayloadJson)
            VALUES (@site, @key, @sub, @serial, @oprun, @step,
                 @equip, @signal, @value, @uom, @actor, @occurred, @recorded, @payload);
            """);
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = record.SiteId;
        command.Parameters.Add("@key", SqlDbType.UniqueIdentifier).Value = record.IdempotencyKey;
        command.Parameters.Add("@sub", SqlDbType.VarChar, 36).Value = record.SubmissionId;
        command.Parameters.Add("@serial", SqlDbType.VarChar, 16).Value = record.SerialNumber;
        command.Parameters.Add("@oprun", SqlDbType.NVarChar, 100).Value = record.OperationRunId;
        command.Parameters.Add("@step", SqlDbType.VarChar, 20).Value = record.StepCode;
        command.Parameters.Add("@equip", SqlDbType.NVarChar, 200).Value = record.EquipmentPath;
        command.Parameters.Add("@signal", SqlDbType.VarChar, 50).Value = record.SignalCode;
        // Value là text round-trip của decimal, KHÔNG ép fixed-scale để không mất chữ số.
        command.Parameters.Add("@value", SqlDbType.NVarChar, 50).Value = record.ValueText;
        command.Parameters.Add("@uom", SqlDbType.VarChar, 20).Value = record.UnitOfMeasure;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = record.ActorId;
        command.Parameters.Add("@occurred", SqlDbType.DateTimeOffset).Value = record.OccurredAt;
        command.Parameters.Add("@recorded", SqlDbType.DateTimeOffset).Value = record.RecordedAt;
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = record.PayloadJson;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
