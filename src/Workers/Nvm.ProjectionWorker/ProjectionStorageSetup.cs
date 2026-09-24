using System.Data;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;
using Npgsql;
using Nvm.Projections;

namespace Nvm.ProjectionWorker;

/// <summary>Explicit deployment job; runtime never receives either database's administrator credentials.</summary>
internal static class ProjectionStorageSetup
{
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "PostgreSQL format %L quotes the parameter; role name and statement template are fixed.")]
    internal static async Task PrepareAsync(string postgresConnection, string sqlConnection,
        string postgresPassword, string sqlPassword)
    {
        ValidatePassword(postgresPassword);
        ValidatePassword(sqlPassword);
        await using var postgres = NpgsqlDataSource.Create(postgresConnection);
        await using (var role = postgres.CreateCommand("""
            SELECT format('CREATE ROLE nvm_projection LOGIN PASSWORD %L', @password)
            WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_projection');
            """))
        {
            role.Parameters.AddWithValue("password", postgresPassword);
            if (await role.ExecuteScalarAsync() is string statement)
            {
                await using var create = postgres.CreateCommand(statement);
                await create.ExecuteNonQueryAsync();
            }
        }
        await ProjectionSchemaMigrator.UpgradeAsync(postgres);
        await using var sql = new SqlConnection(sqlConnection);
        await sql.OpenAsync();
        using var grant = new SqlCommand("""
            IF SUSER_ID('nvm_projection') IS NULL
            BEGIN
                DECLARE @statement nvarchar(max) = N'CREATE LOGIN nvm_projection WITH PASSWORD = '
                    + QUOTENAME(@password, '''') + N', CHECK_POLICY = ON;';
                EXEC sys.sp_executesql @statement;
            END;
            IF DATABASE_PRINCIPAL_ID('nvm_projection') IS NULL
                CREATE USER nvm_projection FOR LOGIN nvm_projection;
            GRANT SELECT ON es.Events TO nvm_projection;
            DENY INSERT, UPDATE, DELETE ON es.Events TO nvm_projection;
            """, sql);
        grant.Parameters.Add("@password", SqlDbType.NVarChar, 128).Value = sqlPassword;
        await grant.ExecuteNonQueryAsync();
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length is < 16 or > 128 || password.StartsWith("CHANGE_ME", StringComparison.Ordinal))
        { throw new InvalidOperationException("Projection role passwords must be private values of 16–128 characters."); }
    }
}
