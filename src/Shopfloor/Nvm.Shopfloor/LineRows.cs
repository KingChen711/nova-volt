using System.Text.Json;
using Npgsql;

namespace Nvm.Shopfloor;

/// <summary>Máy ghi một dòng vào bảng của khu mình. Mã máy, mã bước, mã nguyên liệu là mã phía xưởng.</summary>
internal sealed class LineRows(NpgsqlConnection connection, NpgsqlTransaction? transaction)
{
    /// <summary>Máy phủ ra một cuộn điện cực, kèm lô bột anode và cathode (có hoặc không có lượng thực dùng).</summary>
    public void Coil(int line, string coil, decimal length, string anodeLot, string cathodeLot, decimal? powderQuantity, DateTimeOffset end)
    {
        var lots = JsonSerializer.Serialize(new[]
        {
            new { item = "Anode powder lot in use", lot = anodeLot, qty = powderQuantity },
            new { item = "Cathode powder lot in use", lot = cathodeLot, qty = powderQuantity },
        });
        Insert("""
            INSERT INTO elec.coater_result (line_no, machine, op_code, coil_id, length_m, started_at, finished_at, result, result_code, lots)
            VALUES (@line, 'el010_m1', 'el010', @serial, @length, @start, @end, 'OK', '00', @lots::jsonb);
            """, ("line", line), ("serial", coil), ("length", length), ("start", end.AddMinutes(-4)), ("end", end), ("lots", lots));
    }

    public void Shrink(int line, string lot, DateTimeOffset at) => Insert("""
        INSERT INTO elec.shrink_log (line_no, machine, op_code, item, lot, quantity, unit, logged_at)
        VALUES (@line, 'el010_m1', 'el010', 'Anode powder lot in use', @lot, 0.5, 'kg', @at);
        """, ("line", line), ("lot", lot), ("at", at));

    /// <summary>Máy lắp ra một cell. <paramref name="componentLots"/> rỗng: không quét được lô màng ngăn và vỏ.</summary>
    public void Cell(int line, string family, string cell, string coil, bool ko, string? componentLots, DateTimeOffset end) => Insert("""
        INSERT INTO cell.assembly_result (line_no, machine, op_code, cell_id, started_at, finished_at, judge, judge_code,
            electrode_item, electrode_coil, separator_lot, case_item, case_lot)
        VALUES (@line, 'ca010_m1', 'ca010', @serial, @start, @end, @judge, @code, @electrode, @coil, @separator, @case, @caseLot);
        """, ("line", line), ("serial", cell), ("start", end.AddSeconds(-50)), ("end", end), ("judge", ko ? "KO" : "OK"),
        ("code", ko ? "17" : "00"), ("electrode", $"Electrode coil {family} in use"), ("coil", coil),
        ("separator", componentLots is null ? null : "SP-" + componentLots), ("case", $"Cell case {family} lot in use"),
        ("caseLot", componentLots is null ? null : "CS-" + componentLots));

    public void Formation(int line, string family, string cell, DateTimeOffset end) => Insert("""
        INSERT INTO form.charge_result (line_no, machine, op_code, cell_id, cell_item, started_at, finished_at, result, result_code, ocv_mv)
        VALUES (@line, 'fo010_m1', 'fo010', @serial, @item, @start, @end, 'OK', '00', 3650);
        """, ("line", line), ("serial", cell), ("item", $"Cell {family} in use"), ("start", end.AddMinutes(-50)), ("end", end));

    public void Module(int line, string family, string module, string[] cells, DateTimeOffset end) => Insert("""
        INSERT INTO mod.module_result (line_no, machine, op_code, module_id, cell_item, cell_ids, started_at, finished_at, result, result_code)
        VALUES (@line, 'mo010_m1', 'mo010', @serial, @item, @cells, @start, @end, 'OK', '00');
        """, ("line", line), ("serial", module), ("item", $"Formed cell {family} in use"), ("cells", cells), ("start", end.AddMinutes(-1)),
        ("end", end));

    // SQL cố định; giá trị đi bằng tham số.
#pragma warning disable CA2100
    private void Insert(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                DateTimeOffset time => time.ToUniversalTime(),
                _ => value,
            });
        }

        command.ExecuteNonQuery();
    }
#pragma warning restore CA2100
}
