using Nvm.Foundation;
using Nvm.Foundation.Runtime;

namespace Nvm.Mom.Host;

/// <summary>Phần chạy nền của host: worker nhận event, vòng signal rule, relay gửi lại event chưa lên bus.</summary>
internal sealed partial class FoundationBackgroundService(
    EventWorker worker,
    SignalRuleEngine rules,
    RabbitMqEventPublisher publisher,
    ILogger<FoundationBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan RuleTick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RelayTick = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await worker.StartAsync(stoppingToken).ConfigureAwait(false);
        if (logger.IsEnabled(LogLevel.Information))
        {
            var subscriptions = string.Join(", ", worker.Active.Select(s => s.Subscription));
            WorkerStarted(logger, subscriptions);
        }
        await Task.WhenAll(rules.RunAsync(RuleTick, stoppingToken), RelayAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task RelayAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await publisher.RelayPending(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                if (sent > 0)
                {
                    Relayed(logger, sent);
                }
            }
#pragma warning disable CA1031 // bus hoặc DB chưa sẵn: thử lại nhịp sau
            catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
            {
                RelayFailed(logger, e);
            }

            await Task.Delay(RelayTick, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Event worker started with subscriptions {Subscriptions}")]
    private static partial void WorkerStarted(ILogger logger, string subscriptions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relay published {Count} event(s) that missed the bus at commit")]
    private static partial void Relayed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relay could not publish pending events")]
    private static partial void RelayFailed(ILogger logger, Exception exception);
}

/// <summary>Trace của nền ra log của host (một dòng mỗi bản ghi, có CorrelationId).</summary>
internal sealed partial class LoggingTraceSink(ILogger<LoggingTraceSink> logger) : ITraceSink
{
    public void Write(TraceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var level = record.Level switch
        {
            TraceLevel.Error => LogLevel.Error,
            TraceLevel.Warning => LogLevel.Warning,
            TraceLevel.Information => LogLevel.Information,
            _ => LogLevel.Debug,
        };
        Trace(logger, level, record.CorrelationId, record.ContextName, record.Message, record.CurrentId, record.ParentId);
    }

    [LoggerMessage(Message = "[{CorrelationId}] {Context}: {Message} (current {CurrentId}, parent {ParentId})")]
    private static partial void Trace(ILogger logger, LogLevel level, Guid correlationId, string context, string message,
        Guid currentId, Guid? parentId);
}
