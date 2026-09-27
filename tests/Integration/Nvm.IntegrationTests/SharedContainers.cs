using Microsoft.Data.SqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// Một SQL Server và một TimescaleDB cho cả lượt test; mỗi fixture/test lấy một database riêng (N14). Trước đây mỗi
/// lớp test khởi động SQL Server của nó (~2 GB RAM mỗi container) và mỗi test telemetry khởi động TimescaleDB riêng: hơn
/// 40 container cho một lượt chạy. Container được Ryuk dọn khi process test kết thúc.
/// </summary>
/// <remarks>
/// Không dùng cho test cần thứ ở cấp server: login/role dùng chung tên, dừng container, hay khẳng định trên toàn cluster.
/// Test đó giữ container riêng.
/// </remarks>
internal static class SharedContainers
{
    private static readonly Lazy<Task<MsSqlContainer>> Sql = new(async () =>
    {
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04").Build();
        await container.StartAsync(CancellationToken.None);
        return container;
    });

    private static readonly Lazy<Task<PostgreSqlContainer>> Timescale = new(async () =>
    {
        var container = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
            .WithDatabase("novavolt_integration")
            .WithUsername("nvm")
            .WithPassword("nvm_integration_only")
            .WithCommand("-c", "max_connections=500", "-c", "timescaledb.max_background_workers=64",
                "-c", "max_worker_processes=80")
            .Build();
        await container.StartAsync(CancellationToken.None);
        return container;
    });

    /// <summary>Database SQL Server mới, trống, trên server dùng chung. Trả connection string (quyền sa) tới nó.</summary>
    public static async Task<string> NewSqlDatabaseAsync(string prefix)
    {
        var container = await Sql.Value;
        var name = $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 13, 100)];
        var master = container.GetConnectionString();
        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new SqlCommand("DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC(@sql);",
                connection);
            command.Parameters.AddWithValue("@name", name);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        return new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>Connection string tới master của server dùng chung, cho thao tác cấp server (tạo login).</summary>
    public static async Task<string> SqlServerConnectionStringAsync() => (await Sql.Value).GetConnectionString();

    /// <summary>Database TimescaleDB mới đã có extension timescaledb.</summary>
    public static async Task<SharedTimescaleDatabase> NewTimescaleDatabaseAsync()
    {
        var container = await Timescale.Value;
        var name = "ts_" + Guid.NewGuid().ToString("N")[..16];
        await using (var admin = NpgsqlDataSource.Create(container.GetConnectionString()))
        await using (var create = admin.CreateCommand($"CREATE DATABASE {name};"))
        { await create.ExecuteNonQueryAsync(CancellationToken.None); }
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString;
        await using (var database = NpgsqlDataSource.Create(connectionString))
        await using (var extension = database.CreateCommand("CREATE EXTENSION IF NOT EXISTS timescaledb;"))
        { await extension.ExecuteNonQueryAsync(CancellationToken.None); }
        return new SharedTimescaleDatabase(connectionString);
    }
}

/// <summary>Một database trên TimescaleDB dùng chung; cùng hình dạng API mà test telemetry đã dùng với container riêng.</summary>
internal sealed class SharedTimescaleDatabase(string connectionString) : IAsyncDisposable
{
    public string GetConnectionString() => connectionString;

    /// <summary>Không xoá database: job nền của nó có thể còn chạy; container bị dọn cuối lượt.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
