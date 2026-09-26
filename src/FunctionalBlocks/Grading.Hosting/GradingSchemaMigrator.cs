using Nvm.CommandStore;

namespace Nvm.Grading.Hosting;

/// <summary>Migration tường minh của Grading.</summary>
public static class GradingSchemaMigrator
{
    public static Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default) =>
        EmbeddedSqlMigrations.RunAsync(typeof(GradingSchemaMigrator).Assembly,
            "Nvm.Grading.Hosting.Migrations.", connectionString, cancellationToken);
}
