using System.Data;
using Microsoft.Data.SqlClient;
using Nvm.Foundation;

namespace Nvm.Project;

// Code thành phẩm của team gọi stored procedure bằng tên, qua kết nối SQL riêng lấy từ một configuration key, ngoài
// platform: thiếu key hay thiếu procedure chỉ lộ ra lúc lệnh chạy (lab L17, L18). Procedure là phần cài tay ngoài gói, cùng
// bước với view (make views), Update Database không tạo.

/// <summary>Một MTU bị giữ hoặc bị hủy trong cây thành phần của pallet, kèm NC còn mở nếu có.</summary>
public sealed class PalletHeldItem
{
    public string? PalletNId { get; set; }

    public string? MtuNId { get; set; }

    public string? MaterialNId { get; set; }

    public string? Status { get; set; }

    public string? NonConformanceNId { get; set; }

    public string? NonConformanceStatus { get; set; }

    /// <summary>Tầng trong cây: 1 là hàng trong tote, 2 là thành phần của nó…</summary>
    public int Depth { get; set; }
}

/// <summary>Số liệu của một pallet và năm điều kiểm.</summary>
public sealed class PalletDataRow
{
    public string? PalletNId { get; set; }

    public string? LocationNId { get; set; }

    public string? MaterialNId { get; set; }

    public string? Status { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Số chuẩn của part (property <c>PalletQty</c>).</summary>
    public int? StandardQuantity { get; set; }

    public int Totes { get; set; }

    public int Items { get; set; }

    /// <summary>Số lượng của pallet bằng số chuẩn của part.</summary>
    public bool QuantityOk { get; set; }

    /// <summary>Số tote bằng số lượng của pallet.</summary>
    public bool TotesOk { get; set; }

    /// <summary>Số hàng trong tote bằng số tote.</summary>
    public bool ItemsOk { get; set; }

    /// <summary>Mọi hàng cùng material với pallet.</summary>
    public bool MaterialOk { get; set; }

    /// <summary>Mọi hàng đang Available.</summary>
    public bool AvailableOk { get; set; }
}

/// <summary>Pallet đề xuất lấy theo FIFO: ngày đóng pallet trước, rồi mã pallet.</summary>
public sealed class SuggestedPalletRow
{
    public string? PalletNId { get; set; }

    public string? MaterialNId { get; set; }

    public string? LocationNId { get; set; }

    public DateTimeOffset? BuildDate { get; set; }

    public decimal Quantity { get; set; }
}

internal static class FinishedGoodsSql
{
    public const string HeldItems = "nv_PalletHeldItems";
    public const string PalletData = "nv_PalletData";
    public const string SuggestedPallets = "nv_SuggestedPallets";

    /// <summary>Chuỗi kết nối của team. Thiếu key thì ném lỗi: mọi màn và lệnh dùng procedure đều hỏng (lab L18).</summary>
    public static string Connection(Func<IQueryable<ConfigurationKey>> keys) =>
        keys().Where(k => k.NId == NvKeys.SqlConnection).Select(k => k.Val).SingleOrDefault() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Configuration key {NvKeys.SqlConnection} is missing or empty.");

    public static List<PalletHeldItem> Held(string connection, string palletNId) => Run(connection, HeldItems, r => new PalletHeldItem
    {
        PalletNId = palletNId,
        MtuNId = r.GetString(0),
        MaterialNId = Text(r, 1),
        Status = Text(r, 2),
        NonConformanceNId = Text(r, 3),
        NonConformanceStatus = Text(r, 4),
        Depth = r.GetInt32(5),
    }, ("PalletNId", palletNId));

    public static List<PalletDataRow> Data(string connection, string? palletNId, string? materialNId) => Run(connection, PalletData, r => new PalletDataRow
    {
        PalletNId = r.GetString(0),
        LocationNId = Text(r, 1),
        MaterialNId = Text(r, 2),
        Status = Text(r, 3),
        Quantity = r.GetDecimal(4),
        StandardQuantity = r.IsDBNull(5) ? null : r.GetInt32(5),
        Totes = r.GetInt32(6),
        Items = r.GetInt32(7),
        QuantityOk = r.GetBoolean(8),
        TotesOk = r.GetBoolean(9),
        ItemsOk = r.GetBoolean(10),
        MaterialOk = r.GetBoolean(11),
        AvailableOk = r.GetBoolean(12),
    }, ("PalletNId", palletNId), ("MaterialNId", materialNId));

    public static List<SuggestedPalletRow> Suggested(string connection, string materialNId) => Run(connection, SuggestedPallets, r => new SuggestedPalletRow
    {
        PalletNId = r.GetString(0),
        MaterialNId = Text(r, 1),
        LocationNId = Text(r, 2),
        BuildDate = r.IsDBNull(3) ? null : r.GetDateTimeOffset(3),
        Quantity = r.GetDecimal(4),
    }, ("MaterialNId", materialNId));

    private static string? Text(SqlDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static List<T> Run<T>(string connection, string procedure, Func<SqlDataReader, T> map, params (string Name, string? Value)[] parameters)
    {
        using var sql = new SqlConnection(connection);
        sql.Open();
        // Tên procedure là hằng số của code này; giá trị đi bằng tham số.
#pragma warning disable CA2100
        using var command = new SqlCommand(procedure, sql) { CommandType = CommandType.StoredProcedure };
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new SqlParameter("@" + name, SqlDbType.NVarChar, 255) { Value = (object?)value ?? DBNull.Value });
        }

        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}

/// <summary>
/// SQL của các procedure thành phẩm, cài cùng bước với view. Cùng cú pháp template với <see cref="NvViews"/>:
/// <c>{{table:&lt;Lớp&gt;}}</c>, <c>{{id:&lt;Facet&gt;}}</c>.
/// </summary>
public static class NvProcedures
{
    public static IReadOnlyDictionary<string, string> Templates { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Cây thành phần của pallet: tote → hàng trong tote → genealogy (dòng trừ nguyên liệu) tới 13 tầng. Bảng tạm giữ mã
        // pallet, tote bằng cột 16 ký tự như bản của team: mã dài hơn thì lỗi cắt chuỗi (lab trong bài kiểm pallet).
        [FinishedGoodsSql.HeldItems] = """
            CREATE OR ALTER PROCEDURE dbo.nv_PalletHeldItems @PalletNId NVARCHAR(255)
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @start TABLE (pallet_nid VARCHAR(16) NOT NULL, tote_nid VARCHAR(16) NULL, item_nid NVARCHAR(255) NOT NULL);

                INSERT INTO @start (pallet_nid, tote_nid, item_nid)
                SELECT p.NId, t.NId, m.NId
                FROM dbo.[{{table:Mtua}}] p
                JOIN dbo.[{{table:Mtua}}] t ON t.ParentMtuaNId = p.NId AND t.IsDeleted = 0
                JOIN dbo.[{{table:Mtu}}] m ON m.ParentMtuaNId = t.NId AND m.IsDeleted = 0
                WHERE p.NId = @PalletNId AND p.IsDeleted = 0
                UNION ALL
                SELECT p.NId, NULL, m.NId
                FROM dbo.[{{table:Mtua}}] p
                JOIN dbo.[{{table:Mtu}}] m ON m.ParentMtuaNId = p.NId AND m.IsDeleted = 0
                WHERE p.NId = @PalletNId AND p.IsDeleted = 0;

                WITH tree (nid, depth) AS (
                    SELECT item_nid, 1 FROM @start
                    UNION ALL
                    SELECT c.ComponentMtuNId, tree.depth + 1
                    FROM tree
                    JOIN dbo.[{{table:MaterialConsumption}}] c ON c.ProductMtuNId = tree.nid AND c.IsDeleted = 0
                    WHERE tree.depth < 13
                )
                SELECT m.NId, m.MaterialNId, m.Status, nc.NId, nc.Status, MIN(tree.depth)
                FROM tree
                JOIN dbo.[{{table:Mtu}}] m ON m.NId = tree.nid AND m.IsDeleted = 0
                LEFT JOIN dbo.[{{table:NonConformanceItem}}] i ON i.MtuNId = m.NId AND i.IsDeleted = 0
                LEFT JOIN dbo.[{{table:NonConformance}}] nc ON nc.NId = i.NonConformanceNId AND nc.IsDeleted = 0 AND nc.Status = 'OPEN'
                WHERE m.Status IN ('OnHold', 'Scrapped')
                GROUP BY m.NId, m.MaterialNId, m.Status, nc.NId, nc.Status
                OPTION (MAXRECURSION 100);
            END
            """,

        // Index cho bước lần cây: Update Database chỉ tạo khóa (IsDeleted, NId), nên không có index này thì mỗi tầng của cây quét
        // cả bảng trừ nguyên liệu (đo 2026-10-29: một lần kiểm vượt 120 s trên dữ liệu 30 ngày, benchmarks.md).
        ["IX_MaterialConsumption_Product"] = """
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MaterialConsumption_Product'
                           AND object_id = OBJECT_ID(N'dbo.[{{table:MaterialConsumption}}]'))
                CREATE INDEX IX_MaterialConsumption_Product ON dbo.[{{table:MaterialConsumption}}] (ProductMtuNId, IsDeleted)
                    INCLUDE (ComponentMtuNId);
            """,

        // Năm điều kiểm của một pallet (hoặc mọi pallet của một part).
        [FinishedGoodsSql.PalletData] = """
            CREATE OR ALTER PROCEDURE dbo.nv_PalletData @PalletNId NVARCHAR(255) = NULL, @MaterialNId NVARCHAR(255) = NULL
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT p.NId, p.EquipmentNId, p.MaterialNId, p.Status, p.Quantity, std.qty, totes.n, items.n,
                       CAST(CASE WHEN p.Quantity = std.qty THEN 1 ELSE 0 END AS bit),
                       CAST(CASE WHEN totes.n = p.Quantity THEN 1 ELSE 0 END AS bit),
                       CAST(CASE WHEN items.n = totes.n THEN 1 ELSE 0 END AS bit),
                       CAST(CASE WHEN items.other = 0 THEN 1 ELSE 0 END AS bit),
                       CAST(CASE WHEN items.held = 0 THEN 1 ELSE 0 END AS bit)
                FROM dbo.[{{table:Mtua}}] p
                OUTER APPLY (SELECT TRY_CAST(mp.Value AS int) AS qty FROM dbo.[{{table:MaterialProperty}}] mp
                             WHERE mp.NId = p.MaterialNId + '|PalletQty' AND mp.IsDeleted = 0) std
                OUTER APPLY (SELECT COUNT(*) AS n FROM dbo.[{{table:Mtua}}] t WHERE t.ParentMtuaNId = p.NId AND t.IsDeleted = 0) totes
                OUTER APPLY (SELECT COUNT(*) AS n, ISNULL(SUM(x.other), 0) AS other, ISNULL(SUM(x.held), 0) AS held
                             FROM (SELECT CASE WHEN m.MaterialNId <> p.MaterialNId OR p.MaterialNId IS NULL THEN 1 ELSE 0 END AS other,
                                          CASE WHEN m.Status <> 'Available' THEN 1 ELSE 0 END AS held
                                   FROM dbo.[{{table:Mtua}}] t
                                   JOIN dbo.[{{table:Mtu}}] m ON m.ParentMtuaNId = t.NId AND m.IsDeleted = 0
                                   WHERE t.ParentMtuaNId = p.NId AND t.IsDeleted = 0) x) items
                WHERE p.IsDeleted = 0 AND p.ContainerType = 'Pallet'
                  AND (@PalletNId IS NULL OR p.NId = @PalletNId)
                  AND (@MaterialNId IS NULL OR p.MaterialNId = @MaterialNId)
                ORDER BY p.NId;
            END
            """,

        // Đề xuất theo FIFO: bản ghi FIFO Full ở ô kho của part, theo ngày đóng pallet rồi mã pallet. Nối bảng pallet để lấy số
        // lượng mà quên lọc IsDeleted của bảng đó: pallet đã xóa mềm cùng mã thì đề xuất hai lần (lab L03).
        [FinishedGoodsSql.SuggestedPallets] = """
            CREATE OR ALTER PROCEDURE dbo.nv_SuggestedPallets @MaterialNId NVARCHAR(255)
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT fp.NId, fp.MaterialNId, fp.LocationNId, fp.BuildDate, pt.Quantity
                FROM dbo.[{{table:FifoPallet}}] fp
                JOIN dbo.[{{table:Mtua}}] pt ON pt.NId = fp.NId
                JOIN dbo.[{{table:PartAddress}}] pa ON pa.MaterialNId = fp.MaterialNId AND pa.StoreAddress = fp.LocationNId AND pa.IsDeleted = 0
                WHERE fp.IsDeleted = 0 AND fp.Status = 'Full' AND fp.MaterialNId = @MaterialNId
                ORDER BY fp.BuildDate, fp.NId;
            END
            """,
    };
}
