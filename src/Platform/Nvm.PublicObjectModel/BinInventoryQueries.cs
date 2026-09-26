using Npgsql;

namespace Nvm.PublicObjectModel;

/// <summary>Một cell còn trong kho bin, đủ điều kiện ghép module.</summary>
public sealed record BinInventoryCell(string SerialNumber, string BinCode, decimal CapacityAh, decimal OcvMillivolt,
    decimal DcirMilliOhm, string LotId, DateTimeOffset GradedAt);

/// <summary>Số cell còn trong kho theo bin.</summary>
public sealed record BinCount(string BinCode, int Cells);

/// <summary>
/// Kho bin trên read model: cell đã grade vào bin, chưa lắp vào module, không bị Quality giữ hay loại.
/// Lot là cuộn điện cực (TRANSFORMATION từ roll) của cell; không có thì là <c>UNKNOWN</c>.
/// </summary>
public sealed class BinInventoryQueries(NpgsqlDataSource dataSource)
{
    private const string Available = """
        FROM rm.bin_inventory b
        LEFT JOIN rm.unit_quality q ON q.site_id = b.site_id AND q.serial_number = b.serial_number
        WHERE b.site_id = @site AND b.product_code = @product AND b.bin_code IS NOT NULL AND b.assembled_into IS NULL
          AND coalesce(q.quality_state, 'Pending') NOT IN ('Held', 'Scrapped')
        """;

    public async Task<IReadOnlyList<BinInventoryCell>> AvailableAsync(string siteId, string productCode,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT b.serial_number, b.bin_code, b.capacity_ah, b.ocv_mv, b.dcir_mohm,
                coalesce((SELECT min(g.parent_id) FROM trace.genealogy_link g
                          WHERE g.site_id = b.site_id AND g.child_type = 2 AND g.child_id = b.serial_number
                            AND g.parent_type = 5 AND g.unlinked_at IS NULL), 'UNKNOWN'),
                b.graded_at
            {Available}
            ORDER BY b.bin_code, b.serial_number;
            """);
        command.Parameters.AddWithValue("site", siteId);
        command.Parameters.AddWithValue("product", productCode);
        var cells = new List<BinInventoryCell>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cells.Add(new BinInventoryCell(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2),
                reader.GetDecimal(3), reader.GetDecimal(4), reader.GetString(5),
                await reader.GetFieldValueAsync<DateTimeOffset>(6, cancellationToken).ConfigureAwait(false)));
        }
        return cells;
    }

    public async Task<IReadOnlyList<BinCount>> CountsAsync(string siteId, string productCode, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT b.bin_code, count(*)::int {Available} GROUP BY b.bin_code ORDER BY b.bin_code;
            """);
        command.Parameters.AddWithValue("site", siteId);
        command.Parameters.AddWithValue("product", productCode);
        var counts = new List<BinCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { counts.Add(new BinCount(reader.GetString(0), reader.GetInt32(1))); }
        return counts;
    }
}
