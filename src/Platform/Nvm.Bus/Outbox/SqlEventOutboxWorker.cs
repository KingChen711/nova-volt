using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nvm.EventStore;

namespace Nvm.Bus.Outbox;

/// <summary>Drains committed SQL Server event intents independently of HTTP requests.</summary>
public sealed class SqlEventOutboxWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<SqlEventOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<SqlEventOutboxDispatcher>();
                var result = await dispatcher.DispatchOnceAsync(100, stoppingToken).ConfigureAwait(false);
                if (result.Failed > 0)
                {
                    logger.LogWarning("SQL event outbox batch: {Published} published, {Failed} failed",
                        result.Published, result.Failed);
                }

                if (result.Claimed == 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), clock, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "SQL event outbox poll failed; retrying");
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
