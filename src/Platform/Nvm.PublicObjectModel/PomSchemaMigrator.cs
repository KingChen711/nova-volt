using DbUp;

namespace Nvm.PublicObjectModel;

public static class PomSchemaMigrator
{
    public static void Upgrade(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var result = DeployChanges.To.PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(PomSchemaMigrator).Assembly,
                name => name.Contains(".Migrations.", StringComparison.Ordinal))
            .JournalToPostgresqlTable("public", "nvm_pom_schema_versions")
            .Build().PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException("POM migration failed.", result.Error);
        }
    }
}
