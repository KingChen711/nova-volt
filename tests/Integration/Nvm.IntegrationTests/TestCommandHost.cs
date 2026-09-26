using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Nvm.CommandStore;
using Nvm.Equipment.Commands;
using Nvm.Equipment.Hosting;
using Nvm.EventStore;
using Nvm.Grading.Commands;
using Nvm.Grading.Hosting;
using Nvm.Kernel;
using Nvm.Kernel.Commands;
using Nvm.Material.Commands;
using Nvm.Material.Hosting;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Hosting;
using Nvm.PublicObjectModel;
using Nvm.Quality.Commands;
using Nvm.Quality.Hosting;
using Nvm.Recipe.Commands;
using Nvm.Recipe.Hosting;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;

namespace Nvm.IntegrationTests;

/// <summary>Pipeline command thật (kernel + SQL claim + event store + mọi FB) cho integration test không qua HTTP.</summary>
public static class TestCommandHost
{
    public static async Task MigrateAsync(string connectionString, CancellationToken ct)
    {
        await EventSchemaMigrator.UpgradeAsync(connectionString, ct);
        await ProductionExecutionSchemaMigrator.UpgradeAsync(connectionString, ct);
        await QualitySchemaMigrator.UpgradeAsync(connectionString, ct);
        await GradingSchemaMigrator.UpgradeAsync(connectionString, ct);
        await MaterialSchemaMigrator.UpgradeAsync(connectionString, ct);
        await RecipeSchemaMigrator.UpgradeAsync(connectionString, ct);
        await EquipmentSchemaMigrator.UpgradeAsync(connectionString, ct);
        await TraceabilityFixtureSeed.PrepareAsync(connectionString, ct);
    }

    /// <param name="sqlConnection">SQL Server chứa command store, event store và bảng của các FB.</param>
    /// <param name="clock">Đồng hồ của mọi handler; test dùng <c>FakeTimeProvider</c>.</param>
    /// <param name="postgres">Read model cho các adapter đọc genealogy/POM; null khi test không cần.</param>
    /// <param name="configure">Thay port/adapter riêng cho một test.</param>
    public static ServiceProvider Build(string sqlConnection, TimeProvider clock, NpgsqlDataSource? postgres = null,
        Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NVM_COMMANDS:ConnectionString"] = sqlConnection,
            ["NVM_FORMATION:TimeoutWorker"] = "false",
            ["NVM_QUALITY:CascadeWorker"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.AddSingleton(clock);
        services.AddNvmKernel(typeof(RecordRollCoatedCommand).Assembly, typeof(SerializeUnitCommand).Assembly,
            typeof(ConsumeMaterialCommand).Assembly, typeof(GradeUnitCommand).Assembly, typeof(PlaceHoldCommand).Assembly,
            typeof(ApplyRecipeCommand).Assembly, typeof(RegisterEquipmentCommand).Assembly);
        services.AddNvmCommandStore(configuration, new TestEnvironment());
        services.AddNvmTraceability(configuration);
        services.AddNvmQuality(configuration);
        services.AddNvmMaterial();
        services.AddNvmGrading();
        services.AddNvmRecipe();
        services.AddNvmEquipment();
        services.AddNvmProductionExecutionAdapters(configuration);
        if (postgres is not null)
        {
            services.AddSingleton(postgres);
            services.AddSingleton<TraceQueries>();
            services.AddSingleton<BinInventoryQueries>();
        }
        else
        {
            // Không có read model: hold không lan xuống unit nào (test chỉ cần hold trên chính lot/unit).
            services.Replace(ServiceDescriptor.Scoped<Nvm.Quality.Ports.IDownstreamUnits, NoDownstreamUnits>());
        }
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = postgres is not null, ValidateScopes = true });
    }

    public static async Task<TResult> DispatchAsync<TResult>(this ServiceProvider provider, ICommand<TResult> command,
        CancellationToken ct)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>().DispatchAsync<TResult>(command, ct);
    }

    private sealed class NoDownstreamUnits : Nvm.Quality.Ports.IDownstreamUnits
    {
        public Task<IReadOnlyList<string>> ReadAsync(string siteId, string targetKind, string targetId, decimal? spanFromMeter,
            decimal? spanToMeter, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Nvm.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
