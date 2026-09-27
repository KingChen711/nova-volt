using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nvm.CommandStore;
using Nvm.Kernel.Commands;
using Nvm.MasterData.Hosting;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Hosting;

namespace Nvm.ErpGateway;

/// <summary>
/// Đánh giá lại work order đang chờ master data. Mỗi revision master data là một submission khác, nên mỗi thay đổi
/// master data được thử đúng một lần cho mỗi lệnh; poll khi không có gì đổi chỉ phát lại kết quả cũ.
/// </summary>
public sealed class PendingWorkOrderReevaluator(IServiceScopeFactory scopes, MasterDataQueries masterData,
    WorkOrderQueries workOrders, ErpGatewayOptions options, TimeProvider clock)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var revision = await masterData.RevisionAsync(options.SiteId, cancellationToken).ConfigureAwait(false);
        var pending = await workOrders.ListAsync(options.SiteId, WorkOrderStatuses.PendingMasterData, 500, cancellationToken)
            .ConfigureAwait(false);
        var released = 0;
        foreach (var order in pending)
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>().DispatchAsync<DomainCommandResult>(
                new ReevaluateWorkOrderCommand(options.SiteId, options.ActorId, clock.GetUtcNow(), order.WorkOrderId, revision),
                cancellationToken).ConfigureAwait(false);
            if (result.Accepted)
            { released++; }
        }
        return released;
    }
}

/// <summary>Vòng chạy nền: inbound + đánh giá lại mỗi <c>PollInterval</c>, backflush mỗi <c>BackflushInterval</c>.</summary>
public sealed class ErpGatewayWorker(ErpInboundProcessor inbound, PendingWorkOrderReevaluator reevaluator,
    BackflushPublisher backflush, ErpGatewayOptions options, TimeProvider clock, ILogger<ErpGatewayWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextBackflush = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await inbound.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await reevaluator.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (clock.GetUtcNow() >= nextBackflush)
                {
                    await backflush.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    nextBackflush = clock.GetUtcNow() + options.BackflushInterval;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogError(error, "ERP gateway cycle failed for site {SiteId}", options.SiteId); }
            await Task.Delay(options.PollInterval, clock, stoppingToken).ConfigureAwait(false);
        }
    }
}

public static class ErpGatewayRegistration
{
    /// <summary>Đăng ký sau AddNvmCommandStore, AddNvmMasterData và AddNvmProductionExecutionAdapters.</summary>
    public static IServiceCollection AddNvmErpGateway(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ErpGatewayOptions();
        configuration.GetSection("NVM_ERP").Bind(options);
        services.AddSingleton(options);
        services.AddSingleton<ErpInboundProcessor>();
        services.AddSingleton<PendingWorkOrderReevaluator>();
        services.AddHttpClient<BackflushPublisher>(client => client.Timeout = TimeSpan.FromSeconds(30));
        if (options.Enabled)
        {
            if (string.IsNullOrWhiteSpace(options.SiteId) || string.IsNullOrWhiteSpace(options.RootDirectory))
            { throw new InvalidOperationException("NVM_ERP:SiteId and NVM_ERP:RootDirectory are required when the gateway is enabled."); }
            services.AddHostedService<ErpGatewayWorker>();
        }
        return services;
    }
}

/// <summary>Migration tường minh của ERP gateway.</summary>
public static class ErpGatewaySchemaMigrator
{
    public static Task UpgradeAsync(string connectionString, CancellationToken cancellationToken = default) =>
        EmbeddedSqlMigrations.RunAsync(typeof(ErpGatewaySchemaMigrator).Assembly, "Nvm.ErpGateway.Migrations.",
            connectionString, cancellationToken);
}
