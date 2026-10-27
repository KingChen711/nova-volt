using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Nvm.Foundation.Runtime;

/// <summary>
/// "Update Database": căn schema của DB MOM theo model. Tạo bảng và cột còn thiếu; không xóa cột, không đụng dữ liệu.
/// </summary>
/// <remarks>
/// Không dùng migration EF: như nền thật, schema là hệ quả của model, không phải một chuỗi script viết tay. Thứ không
/// có trong model (bản vá tay vào bảng, view, stored procedure) không do bước này quản lý (lab L22 ở R12).
/// </remarks>
public static class SchemaUpdater
{
    public const string EventTable = "Foundation_EventLog";
    public const string ModelTable = "Foundation_ModelTable";

    public static void UpdateDatabase(string connectionString, ModelRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        // ProjectionQuery đọc ngoài transaction trên kết nối riêng. Không có RCSI, nó sẽ chờ khóa của chính transaction
        // đang gọi nó. Có RCSI, nó đọc bản đã commit gần nhất: đúng hành vi "không thấy thay đổi chưa commit".
        Execute(connection, """
            IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()) = 0
                ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
            """);

        foreach (var statement in Statements(registry))
        {
            Execute(connection, statement);
        }
    }

    internal static IEnumerable<string> Statements(ModelRegistry registry)
    {
        yield return $"""
            IF OBJECT_ID(N'dbo.{EventTable}', N'U') IS NULL
            CREATE TABLE dbo.{EventTable} (
                Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_{EventTable} PRIMARY KEY,
                EventId uniqueidentifier NOT NULL,
                EventType nvarchar(256) NOT NULL,
                EventVersion int NOT NULL,
                Payload nvarchar(max) NOT NULL,
                CorrelationId uniqueidentifier NOT NULL,
                RaisedOn datetimeoffset(7) NOT NULL);
            """;
        // Null: chưa lên bus. Relay gửi lại các dòng này (bus chết lúc commit, host chết giữa commit và publish).
        yield return $"""
            IF COL_LENGTH(N'dbo.{EventTable}', N'PublishedOn') IS NULL
                ALTER TABLE dbo.{EventTable} ADD PublishedOn datetimeoffset(7) NULL;
            """;
        yield return $"""
            IF OBJECT_ID(N'dbo.{ModelTable}', N'U') IS NULL
            CREATE TABLE dbo.{ModelTable} (
                ModelName nvarchar(256) NOT NULL CONSTRAINT PK_{ModelTable} PRIMARY KEY,
                Kind nvarchar(16) NOT NULL,
                TableName sysname NOT NULL);
            """;

        foreach (var entity in registry.Entities)
        {
            yield return CreateTable(entity.TableName, "NId nvarchar(255) NOT NULL,");
            foreach (var p in entity.Properties)
            {
                yield return AddColumn(entity.TableName, p, sparse: false);
            }

            yield return UniqueIndex(entity.TableName, "IsDeleted, NId");
            yield return RecordTable(entity.Block.Name + "." + entity.Name, "Entity", entity.TableName);
        }

        foreach (var table in registry.FacetTables)
        {
            yield return CreateTable(table.TableName,
                $"[{table.EntityIdColumn}] uniqueidentifier NOT NULL, FacetType nvarchar(128) NOT NULL,");
            foreach (var facet in table.Facets)
            {
                foreach (var p in facet.Properties)
                {
                    yield return AddColumn(table.TableName, p, sparse: true);
                }

                yield return RecordTable(facet.Block.Name + "." + facet.Name, "Facet", table.TableName);
            }

            yield return UniqueIndex(table.TableName, $"IsDeleted, [{table.EntityIdColumn}], FacetType");
        }
    }

    private static string CreateTable(string table, string keyColumns) => $"""
        IF OBJECT_ID(N'dbo.[{table}]', N'U') IS NULL
        CREATE TABLE dbo.[{table}] (
            Id uniqueidentifier NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY,
            {keyColumns}
            CreatedOn datetimeoffset(7) NOT NULL,
            LastUpdatedOn datetimeoffset(7) NOT NULL,
            OptimisticVersion rowversion NOT NULL,
            IsDeleted int NOT NULL CONSTRAINT [DF_{table}_IsDeleted] DEFAULT 0);
        """;

    // Cột thuộc tính luôn nullable, kể cả [Mandatory]: ràng buộc nằm ở tầng model.
    private static string AddColumn(string table, PropertyModel p, bool sparse) => $"""
        IF COL_LENGTH(N'dbo.[{table}]', N'{p.ColumnName}') IS NULL
            ALTER TABLE dbo.[{table}] ADD [{p.ColumnName}] {SqlType(p)}{(sparse ? " SPARSE" : string.Empty)} NULL;
        """;

    private static string UniqueIndex(string table, string columns) => $"""
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_{table}' AND object_id = OBJECT_ID(N'dbo.[{table}]'))
            CREATE UNIQUE INDEX [UX_{table}] ON dbo.[{table}] ({columns});
        """;

    private static string RecordTable(string modelName, string kind, string table) => $"""
        MERGE dbo.{ModelTable} AS t
        USING (SELECT N'{modelName}' AS ModelName, N'{kind}' AS Kind, N'{table}' AS TableName) AS s ON t.ModelName = s.ModelName
        WHEN MATCHED THEN UPDATE SET Kind = s.Kind, TableName = s.TableName
        WHEN NOT MATCHED THEN INSERT (ModelName, Kind, TableName) VALUES (s.ModelName, s.Kind, s.TableName);
        """;

    private static string SqlType(PropertyModel p)
    {
        var clr = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
        return clr switch
        {
            _ when clr == typeof(string) => p.TextLength > 0
                ? "nvarchar(" + p.TextLength.ToString(CultureInfo.InvariantCulture) + ")"
                : "nvarchar(max)",
            _ when clr == typeof(int) => "int",
            _ when clr == typeof(long) => "bigint",
            _ when clr == typeof(decimal) => "decimal(18,6)",
            _ when clr == typeof(double) => "float",
            _ when clr == typeof(bool) => "bit",
            _ when clr == typeof(Guid) => "uniqueidentifier",
            _ when clr == typeof(DateTimeOffset) => "datetimeoffset(7)",
            _ => throw new NotSupportedException(clr.Name),
        };
    }

    // Câu lệnh chỉ ghép từ tên trong model đã kiểm ở registry (định danh C#), không từ dữ liệu người dùng.
#pragma warning disable CA2100
    private static void Execute(SqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
#pragma warning restore CA2100

}
