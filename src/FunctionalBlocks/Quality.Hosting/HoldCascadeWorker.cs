using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nvm.CommandStore;
using Nvm.Kernel.Commands;
using Nvm.Quality.Commands;

namespace Nvm.Quality.Hosting;

/// <summary>Cấu hình cascade: nghỉ giữa các chunk để nhường tài nguyên cho luồng realtime.</summary>
public sealed record HoldCascadeOptions(TimeSpan DelayBetweenChunks, bool Enabled = true);

/// <summary>
/// Lan hold hạ nguồn (scope §6.7): chốt danh sách unit từ read model genealogy, áp dụng từng chunk 1.000 unit trong
/// transaction riêng với checkpoint, rồi chốt lại một lần nữa để bắt liên kết projection đến muộn. Mỗi bước là một
/// command có key tất định, nên dừng giữa chừng rồi chạy lại sẽ tiếp tục từ checkpoint, không làm lại từ đầu.
/// </summary>
public sealed class HoldCascadeWorker(IServiceScopeFactory scopes, SqlCommandStoreOptions options,
    HoldCascadeOptions cascade, TimeProvider clock, ILogger<HoldCascadeWorker> logger) : BackgroundService
{
    public const int PlanRounds = 2;

    private sealed record Pending(string SiteId, string HoldId, string? JobStatus, int NextChunk, int TotalUnits,
        int ChunkSize, int Rounds);

    /// <summary>Xử lý hết việc đang chờ; trả số chunk đã áp dụng.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var applied = 0;
        foreach (var item in await PendingAsync(cancellationToken).ConfigureAwait(false))
        {
            var rounds = item.Rounds;
            var next = item.NextChunk;
            var total = item.TotalUnits;
            var size = item.ChunkSize == 0 ? 1000 : item.ChunkSize;
            while (rounds < PlanRounds || next * size < total || (total == 0 && item.JobStatus != "Completed" && rounds > 0 && next == 0))
            {
                if (next * size >= total && rounds < PlanRounds)
                {
                    var plan = await DispatchAsync(new PlanHoldCascadeCommand(item.SiteId, item.HoldId, rounds,
                        clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    if (!plan.Accepted)
                    { break; }
                    total = (int)(plan.StreamVersion ?? 0);
                    rounds++;
                    if (total == 0 && next == 0)
                    {
                        await DispatchAsync(new ApplyCascadeChunkCommand(item.SiteId, item.HoldId, 0, clock.GetUtcNow()),
                            cancellationToken).ConfigureAwait(false);
                        next = 1;
                    }
                    continue;
                }
                if (next * size >= total)
                { break; }
                var result = await DispatchAsync(new ApplyCascadeChunkCommand(item.SiteId, item.HoldId, next,
                    clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                if (!result.Accepted)
                { break; }
                next++;
                applied++;
                if (cascade.DelayBetweenChunks > TimeSpan.Zero)
                { await Task.Delay(cascade.DelayBetweenChunks, clock, cancellationToken).ConfigureAwait(false); }
            }
        }
        return applied;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cascade.Enabled)
        { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            { await RunOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { return; }
            catch (Exception error)
            { logger.LogError(error, "Hold cascade pass failed; jobs resume from their checkpoint"); }
            await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<DomainCommandResult> DispatchAsync(ICommand<DomainCommandResult> command, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>()
            .DispatchAsync<DomainCommandResult>(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<Pending>> PendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT h.SiteId, h.HoldId, j.Status, coalesce(j.NextChunk, 0), coalesce(j.TotalUnits, 0),
                coalesce(j.ChunkSize, 0), coalesce(j.PlanRounds, 0)
            FROM quality.Holds h
            LEFT JOIN quality.CascadeJobs j ON j.SiteId = h.SiteId AND j.JobId = h.HoldId
            WHERE h.Status = 'Active' AND (j.JobId IS NULL OR j.Status = 'Running' OR j.PlanRounds < @rounds)
            ORDER BY h.PlacedAt;
            """, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        command.Parameters.Add("@rounds", SqlDbType.Int).Value = PlanRounds;
        var pending = new List<Pending>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pending.Add(new Pending(reader.GetString(0), reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
        }
        return pending;
    }
}
