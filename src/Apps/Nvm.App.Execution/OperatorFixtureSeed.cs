using Npgsql;

namespace Nvm.App.Execution;

internal static class OperatorFixtureSeed
{
    /// <summary>
    /// Insert fixture (idempotent) trên một connection/transaction đã mở. Dùng chung cho CLI seed và test,
    /// nên chỉ có một đường ghi từ một nguồn generator duy nhất.
    /// </summary>
    public static async Task<(int Units, int Wip)> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<OperatorFixtureUnit> units)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        var unitsInserted = 0;
        foreach (var unit in units)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO pom.production_units(
                    id, site_id, serial_number, unit_kind, line, resource, equipment_path,
                    work_order_id, operation_run_id, step_code,
                    execution_state, quality_state, location_state,
                    blocking_reason_code, blocking_reason_text, revision)
                VALUES (@id, @site, @serial, @kind, @line, @resource, @path,
                    @wo, @oprun, @step, @exec, @quality, @location, @blockCode, @blockText, @revision)
                ON CONFLICT (id) DO NOTHING;
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", unit.Id);
            insert.Parameters.AddWithValue("site", unit.SiteId);
            insert.Parameters.AddWithValue("serial", unit.SerialNumber);
            insert.Parameters.AddWithValue("kind", unit.UnitKind);
            insert.Parameters.AddWithValue("line", unit.Line);
            insert.Parameters.AddWithValue("resource", unit.Resource);
            insert.Parameters.AddWithValue("path", unit.EquipmentPath);
            insert.Parameters.AddWithValue("wo", unit.WorkOrderId);
            insert.Parameters.AddWithValue("oprun", unit.OperationRunId);
            insert.Parameters.AddWithValue("step", unit.StepCode);
            insert.Parameters.AddWithValue("exec", unit.ExecutionState);
            insert.Parameters.AddWithValue("quality", unit.QualityState);
            insert.Parameters.AddWithValue("location", unit.LocationState);
            insert.Parameters.AddWithValue("blockCode", (object?)unit.BlockingReasonCode ?? DBNull.Value);
            insert.Parameters.AddWithValue("blockText", (object?)unit.BlockingReasonText ?? DBNull.Value);
            insert.Parameters.AddWithValue("revision", unit.Revision);
            unitsInserted += await insert.ExecuteNonQueryAsync();
        }

        var wipInserted = 0;
        foreach (var row in OperatorFixture.BuildWipRows(units))
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO pom.wip_board(id, site_id, line, step_code, quality_state, unit_count, revision)
                VALUES (@id, @site, @line, @step, @quality, @count, @revision)
                ON CONFLICT (id) DO NOTHING;
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", row.Id);
            insert.Parameters.AddWithValue("site", row.SiteId);
            insert.Parameters.AddWithValue("line", row.Line);
            insert.Parameters.AddWithValue("step", row.StepCode);
            insert.Parameters.AddWithValue("quality", row.QualityState);
            insert.Parameters.AddWithValue("count", row.UnitCount);
            insert.Parameters.AddWithValue("revision", row.Revision);
            wipInserted += await insert.ExecuteNonQueryAsync();
        }

        return (unitsInserted, wipInserted);
    }
}
