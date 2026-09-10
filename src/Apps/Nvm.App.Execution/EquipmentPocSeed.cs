using System.Globalization;
using System.Text.Json;
using Npgsql;
using Nvm.PublicObjectModel;

namespace Nvm.App.Execution;

internal static class EquipmentPocSeed
{
    public static async Task PrepareAsync(IConfiguration configuration)
    {
        var password = configuration["NVM_POM_PASSWORD"];
        if (string.IsNullOrWhiteSpace(password) || password.StartsWith("CHANGE_ME", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Set NVM_POM_PASSWORD before preparing the PoC.");
        }

        var sourcePath = configuration["NVM_POM:FactoryModelPath"]
            ?? throw new InvalidOperationException("NVM_POM:FactoryModelPath must point to factory-model.r3.json.");
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(sourcePath));
        if (source.RootElement.GetProperty("revision").GetInt32() != 3)
        {
            throw new InvalidOperationException("M4 Equipment PoC requires factory model revision 3.");
        }

        var connectionString = configuration["NVM_POM:MigrationConnectionString"]
            ?? new NpgsqlConnectionStringBuilder
            {
                Host = "localhost",
                Port = int.Parse(configuration["NVM_PORT_POSTGRES"] ?? "5432", CultureInfo.InvariantCulture),
                Database = configuration["NVM_POSTGRES_DB"] ?? "novavolt",
                Username = configuration["NVM_POSTGRES_USER"] ?? "nvm",
                Password = configuration["NVM_POSTGRES_PASSWORD"],
                GssEncryptionMode = GssEncryptionMode.Disable,
            }.ConnectionString;
        PomSchemaMigrator.Upgrade(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // Chỉ tạo credential lần đầu; chạy seed lại không xoay mật khẩu role đang được runtime dùng.
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

        await using (var grant = new NpgsqlCommand("""
            GRANT USAGE ON SCHEMA pom TO nvm_pom;
            GRANT SELECT ON pom.equipment TO nvm_pom;
            """, connection, transaction))
        {
            await grant.ExecuteNonQueryAsync();
        }

        var enterprise = source.RootElement.GetProperty("enterprise");
        var count = 0;
        foreach (var site in enterprise.GetProperty("children").EnumerateArray())
        {
            var siteId = site.GetProperty("code").GetString()!;
            foreach (var area in site.GetProperty("children").EnumerateArray()
                .Where(area => area.GetProperty("code").GetString() is "PACK" or "MODULE"))
            {
                foreach (var line in area.GetProperty("children").EnumerateArray())
                {
                    foreach (var resource in line.GetProperty("children").EnumerateArray())
                    {
                        var lineCode = line.GetProperty("code").GetString()!;
                        var resourceCode = resource.GetProperty("code").GetString()!;
                        var path = $"{enterprise.GetProperty("code").GetString()}/{siteId}/{area.GetProperty("code").GetString()}/{lineCode}/{resourceCode}";
                        await using var insert = new NpgsqlCommand("""
                            INSERT INTO pom.equipment(id, site_id, equipment_path, name, line, resource, revision)
                            VALUES (@id, @site, @path, @name, @line, @resource, 3)
                            ON CONFLICT (id) DO NOTHING;
                            """, connection, transaction);
                        insert.Parameters.AddWithValue("id", path.Replace('/', '-'));
                        insert.Parameters.AddWithValue("site", siteId);
                        insert.Parameters.AddWithValue("path", path);
                        insert.Parameters.AddWithValue("name", resource.GetProperty("name").GetString()!);
                        insert.Parameters.AddWithValue("line", lineCode);
                        insert.Parameters.AddWithValue("resource", resourceCode);
                        count += await insert.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        await transaction.CommitAsync();
        Console.WriteLine(FormattableString.Invariant($"Equipment PoC prepared; inserted {count} rows. Existing rows and credentials preserved."));
    }
}
