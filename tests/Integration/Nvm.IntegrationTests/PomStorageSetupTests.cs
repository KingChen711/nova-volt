using Npgsql;
using Nvm.App.Execution;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// Migration production của POM (<c>--migrate</c>) phải để lại một role runtime đọc được cả ba read model. Smoke kind
/// trên database trống lộ ra rằng trước đây chỉ fixture Development tạo role này, nên execution không bao giờ ready.
/// </summary>
public sealed class PomStorageSetupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrate_OnAnEmptyDatabase_LeavesAReadOnlyRuntimeRole_AndRerunDoesNotRotateIt()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync(Ct);
        var admin = postgres.GetConnectionString();
        var password = "pom-" + Guid.NewGuid().ToString("N");

        await PomStorageSetup.PrepareAsync(admin, password);
        await PomStorageSetup.PrepareAsync(admin, "pom-other-" + Guid.NewGuid().ToString("N"));   // chạy lại: giữ mật khẩu cũ

        var runtime = new NpgsqlConnectionStringBuilder(admin) { Username = "nvm_pom", Password = password }.ConnectionString;
        await using var connection = new NpgsqlConnection(runtime);
        await connection.OpenAsync(Ct);
        foreach (var table in new[] { "pom.equipment", "pom.production_units", "pom.wip_board",
            "pom.production_units_read", "pom.wip_board_read" })
        {
#pragma warning disable CA2100 // tên bảng là hằng trong test
            await using var select = new NpgsqlCommand($"SELECT count(*) FROM {table};", connection);
#pragma warning restore CA2100
            (await select.ExecuteScalarAsync(Ct)).ShouldBe(0L);
        }
        await using var write = new NpgsqlCommand("DELETE FROM pom.equipment;", connection);
        (await Should.ThrowAsync<PostgresException>(() => write.ExecuteNonQueryAsync(Ct))).SqlState.ShouldBe("42501");
        await Should.ThrowAsync<InvalidOperationException>(() => PomStorageSetup.PrepareAsync(admin, "CHANGE_ME"));
    }
}
