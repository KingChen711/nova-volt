using Npgsql;
using Nvm.PublicObjectModel;

namespace Nvm.App.Execution;

/// <summary>
/// Schema POM cộng role chỉ đọc <c>nvm_pom</c> mà runtime dùng. Dùng chung cho migration production (<c>--migrate</c>) và
/// fixture Development: trước đây chỉ fixture tạo role, nên một deploy mới qua <c>--migrate</c> không bao giờ ready.
/// </summary>
public static class PomStorageSetup
{
    public static async Task PrepareAsync(string migrationConnectionString, string? runtimePassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationConnectionString);
        if (string.IsNullOrWhiteSpace(runtimePassword) || runtimePassword.StartsWith("CHANGE_ME", StringComparison.Ordinal))
        { throw new InvalidOperationException("Set NVM_POM_PASSWORD for the nvm_pom runtime role."); }
        PomSchemaMigrator.Upgrade(migrationConnectionString);
        await using var connection = new NpgsqlConnection(migrationConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ProvisionRuntimeRoleAsync(connection, transaction, runtimePassword);
        await transaction.CommitAsync();
    }

    /// <summary>Tạo role lần đầu (không xoay mật khẩu role đang được runtime dùng) và cấp SELECT trên read model.</summary>
    public static async Task ProvisionRuntimeRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string password)
    {
        await using (var role = new NpgsqlCommand("""
            SELECT format('CREATE ROLE nvm_pom LOGIN PASSWORD %L', @password)
            WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_pom');
            """, connection, transaction))
        {
            role.Parameters.AddWithValue("password", password);
            if (await role.ExecuteScalarAsync() is string createRole)
            {
                // PostgreSQL đã quote password bằng %L; tên role và câu DDL là hằng, không nhận SQL từ client.
#pragma warning disable CA2100
                await using var create = new NpgsqlCommand(createRole, connection, transaction);
#pragma warning restore CA2100
                await create.ExecuteNonQueryAsync();
            }
        }

        await using var grant = new NpgsqlCommand("""
            GRANT USAGE ON SCHEMA pom TO nvm_pom;
            GRANT SELECT ON pom.equipment, pom.production_units, pom.wip_board TO nvm_pom;
            GRANT SELECT ON pom.production_units_read, pom.wip_board_read TO nvm_pom;
            """, connection, transaction);
        await grant.ExecuteNonQueryAsync();
    }
}
