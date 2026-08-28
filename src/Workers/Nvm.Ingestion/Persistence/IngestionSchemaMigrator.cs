using System.Reflection;
using DbUp;

namespace Nvm.Ingestion.Persistence;

/// <summary>Runs versioned PostgreSQL scripts only when invoked as a migration job.</summary>
public static class IngestionSchemaMigrator
{
    private const string UpScriptMarker = ".Migrations.Up.";

    /// <summary>Applies every forward migration not yet journalled.</summary>
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
