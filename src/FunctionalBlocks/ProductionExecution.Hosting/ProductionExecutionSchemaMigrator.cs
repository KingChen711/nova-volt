using Nvm.CommandStore;

namespace Nvm.ProductionExecution.Hosting;

/// <summary>Migration tường minh của ProductionExecution (formation/aging); chạy sau CommandSchemaMigrator.</summary>
public static class ProductionExecutionSchemaMigrator
{
    public static Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default) =>
        EmbeddedSqlMigrations.RunAsync(typeof(ProductionExecutionSchemaMigrator).Assembly,
            "Nvm.ProductionExecution.Hosting.Migrations.", connectionString, cancellationToken);
}
