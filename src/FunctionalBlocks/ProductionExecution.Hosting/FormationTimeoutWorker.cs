using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nvm.Kernel.Commands;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Ports;

namespace Nvm.ProductionExecution.Hosting;

/// <summary>
/// Bắn timeout đến hạn theo <see cref="TimeProvider"/>. Hạn nằm trong DB nên restart không mất; mỗi lần bắn
/// là một command có idempotency key tất định, nên hai worker cùng bắn chỉ tạo một kết quả.
/// </summary>
public sealed class FormationTimeoutWorker(IServiceScopeFactory scopes, IDueTimeoutSource source, TimeProvider clock,
    ILogger<FormationTimeoutWorker> logger) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Một lượt: đọc timeout đến hạn tại thời điểm hiện tại và bắn từng cái; trả số đã xử lý.</summary>
    /// <remarks>
    /// Timeout của các cell khác nhau độc lập (mỗi cell một dòng trạng thái), nên được bắn song song có giới
    /// hạn. Giới hạn nhỏ để một đợt 30.000 cell tới hạn cùng lúc không chiếm hết connection của command operator.
    /// </remarks>
    public async Task<int> RunOnceAsync(int limit = 500, int parallelism = 8,
        CancellationToken cancellationToken = default)
    {
        var due = await source.ReadDueAsync(clock.GetUtcNow(), limit, cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(due, new ParallelOptions
        { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, async (timeout, ct) =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
            await dispatcher.DispatchAsync<DomainCommandResult>(new FireFormationTimeoutCommand(timeout.SiteId,
                timeout.SerialNumber, timeout.Kind, timeout.DueAt), ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return due.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            { processed = await RunOnceAsync(cancellationToken: stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { return; }
            catch (Exception error)
            { logger.LogError(error, "Formation timeout pass failed; due timeouts stay pending"); }
            if (processed == 0)
            { await Task.Delay(PollInterval, clock, stoppingToken).ConfigureAwait(false); }
        }
    }
}
