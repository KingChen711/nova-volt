using Nvm.CommandStore;

namespace Nvm.Quality.Hosting;

/// <summary>Migration tường minh bằng credential chủ schema, ngoài luồng request.</summary>
public static class QualitySchemaMigrator
{
    public static Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default) =>
        EmbeddedSqlMigrations.RunAsync(typeof(QualitySchemaMigrator).Assembly,
            "Nvm.Quality.Hosting.Migrations.", connectionString, cancellationToken);
}
