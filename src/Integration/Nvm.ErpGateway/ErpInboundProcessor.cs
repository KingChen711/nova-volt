using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nvm.CommandStore;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;
using Nvm.ProductionExecution.Commands;

namespace Nvm.ErpGateway;

/// <summary>Cấu hình gateway ERP của một site (mục <c>NVM_ERP</c>).</summary>
public sealed class ErpGatewayOptions
{
    public bool Enabled { get; set; }

    public string SiteId { get; set; } = "";

    /// <summary>Thư mục gốc chứa <c>inbound/</c>, <c>processed/</c>, <c>rejected/</c> (thư mục SFTP của ERP).</summary>
    public string RootDirectory { get; set; } = "";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan BackflushInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Địa chỉ REST của ERP nhận backflush; null thì không đẩy.</summary>
    public Uri? ErpBaseAddress { get; set; }

    public string ActorId { get; set; } = "system:erp-gateway";

    public string Inbound => Path.Combine(RootDirectory, "inbound");

    public string Processed => Path.Combine(RootDirectory, "processed");

    public string Rejected => Path.Combine(RootDirectory, "rejected");
}

public sealed record InboundRunSummary(int Files, int Processed, int Rejected, int Deferred, int WorkOrders);

/// <summary>
/// Xử lý tuần tự các file B2MML trong <c>inbound/</c>. File hỏng vào <c>rejected/</c> kèm <c>.error.txt</c> và vòng xử lý
/// tiếp tục; lỗi hạ tầng (DB) để file lại cho lượt sau. Cùng lịch gửi lại chỉ phát lại kết quả cũ (idempotent theo
/// ScheduleId/WorkOrderId).
/// </summary>
public sealed class ErpInboundProcessor(IServiceScopeFactory scopes, ErpGatewayOptions options, TimeProvider clock,
    ILogger<ErpInboundProcessor> logger)
{
    public async Task<InboundRunSummary> RunOnceAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Inbound);
        Directory.CreateDirectory(options.Processed);
        Directory.CreateDirectory(options.Rejected);
        var files = Directory.EnumerateFiles(options.Inbound, "*.xml").Order(StringComparer.Ordinal).ToArray();
        int processed = 0, rejected = 0, deferred = 0, workOrders = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            B2mmlParseResult parsed;
            try
            {
                // Mở độc quyền: ERP còn đang ghi thì để lượt sau.
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                parsed = B2mmlParser.Parse(stream);
            }
            catch (IOException)
            {
                deferred++;
                continue;
            }
            if (!parsed.IsValid)
            {
                await RejectAsync(file, parsed.Errors, cancellationToken).ConfigureAwait(false);
                rejected++;
                continue;
            }
            List<string> conflicts = [];
            try
            {
                foreach (var request in parsed.Schedule!.Requests)
                {
                    var result = await DispatchAsync(new ReceiveWorkOrderCommand(options.SiteId, options.ActorId,
                        clock.GetUtcNow(), parsed.Schedule.ScheduleId, request.WorkOrderId, request.ProductCode,
                        request.EarliestStart, request.Materials), cancellationToken).ConfigureAwait(false);
                    if (result.Accepted)
                    { workOrders++; }
                    else
                    { conflicts.Add($"{request.WorkOrderId}: {result.ReasonCode} {result.ReasonText}"); }
                }
            }
            catch (CommandValidationException error)
            {
                conflicts.AddRange(error.Failures.Select(f => $"{f.Field}: {f.Message}"));
                if (conflicts.Count == 0)
                { conflicts.Add(error.Message); }
            }
            catch (InvalidOperationException error) when (string.Equals(error.Message,
                       DurableCommandHttp.IdentityConflictMessage, StringComparison.Ordinal))
            {
                // Cùng ScheduleId/WorkOrderId nhưng nội dung khác lần trước: ERP gửi lại lịch đã sửa mà không đổi ID.
                conflicts.Add("Lịch này đã nhận với nội dung khác; ERP phải phát lịch mới với ID mới.");
            }
            catch (Exception error) when (error is not (OperationCanceledException or CommandHandlerNotFoundException))
            {
                // Hạ tầng lỗi: không di chuyển file, lượt sau thử lại. Dừng vòng này để không dội DB đang hỏng.
                logger.LogError(error, "ERP inbound {File} deferred: command store unavailable", Path.GetFileName(file));
                deferred++;
                break;
            }
            if (conflicts.Count > 0)
            {
                await RejectAsync(file, conflicts, cancellationToken).ConfigureAwait(false);
                rejected++;
                continue;
            }
            File.Move(file, UniquePath(options.Processed, Path.GetFileName(file)));
            processed++;
        }
        return new InboundRunSummary(files.Length, processed, rejected, deferred, workOrders);
    }

    private async Task<DomainCommandResult> DispatchAsync(ReceiveWorkOrderCommand command, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>()
            .DispatchAsync<DomainCommandResult>(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task RejectAsync(string file, IEnumerable<string> errors, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(file);
        var target = UniquePath(options.Rejected, name);
        File.Move(file, target);
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"File: {name}")
            .AppendLine(CultureInfo.InvariantCulture, $"Từ chối lúc: {clock.GetUtcNow():O}")
            .AppendLine("Lý do:");
        foreach (var error in errors)
        { text.AppendLine(CultureInfo.InvariantCulture, $"- {error}"); }
        await File.WriteAllTextAsync(target + ".error.txt", text.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        logger.LogWarning("ERP inbound {File} rejected", name);
    }

    private string UniquePath(string directory, string name)
    {
        var target = Path.Combine(directory, name);
        return File.Exists(target)
            ? Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}.{clock.GetUtcNow():yyyyMMddHHmmssfffffff}{Path.GetExtension(name)}")
            : target;
    }
}
