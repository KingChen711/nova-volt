using System.Reflection;
using DbUp;

namespace Nvm.Ingestion.Persistence;

/// <summary>Chạy các script PostgreSQL đã đánh version, chỉ khi được gọi như một migration job.</summary>
public static class IngestionSchemaMigrator
{
    private const string UpScriptMarker = ".Migrations.Up.";

    /// <summary>Áp dụng mọi forward migration chưa được journal.</summary>
    public static void Upgrade(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var result = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.Contains(UpScriptMarker, StringComparison.Ordinal))
            .JournalToPostgresqlTable("public", "nvm_ingestion_schema_versions")
            .LogToConsole()
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException("PostgreSQL ingestion migration failed.", result.Error);
        }
    }
}
