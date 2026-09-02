namespace Nvm.Kernel.Commands.Audit;

/// <summary>Ghi lại rằng một command đã được thực hiện, mất bao lâu, và có thành công hay không.</summary>
/// <typeparam name="TCommand">Command đang được xử lý.</typeparam>
/// <typeparam name="TResult">Kết quả trả về khi xử lý.</typeparam>
/// <param name="sink">Nơi các entry được ghi tới.</param>
/// <param name="clock">Đồng hồ duy nhất. Không bao giờ dùng <c>DateTimeOffset.UtcNow</c> (AGENTS.md K1).</param>
/// <remarks>
/// <para>
/// Là behavior trong cùng nhất trong ba behavior, nên nó chỉ bọc quanh handler và không gì khác. Vị trí
/// này kéo theo một hệ quả cần nói rõ thay vì để tự phát hiện: một command bị validation từ chối, và
/// một bản duplicate bị chặn sớm ở phía trên, <b>đều không xuất hiện trong audit trail</b>.
/// </para>
/// <para>
/// Đó chính là cách audit trail ở đây được hiểu — một hồ sơ về những gì nhà máy thực sự đã làm với sản
/// phẩm, không phải hồ sơ về mọi request đã đến. Một bản duplicate không làm thay đổi gì, nên nó cũng
/// không có gì để ghi lại; nó được đếm như một metric thay vì vậy. Khi chữ ký điện tử được đưa vào và
/// các lần thử bị từ chối trở thành điều đáng quan tâm riêng, chúng sẽ có trail riêng thay vì bị trộn
/// vào trail này.
/// </para>
/// <para>
/// Thất bại được ghi lại rồi mới ném lại (rethrow). Một trail chỉ chứa toàn thành công thì không trả
/// lời được câu hỏi mà một cuộc điều tra thực sự bắt đầu bằng — đó là đã thử gì và không thành công.
/// </para>
/// </remarks>
public sealed class AuditBehavior<TCommand, TResult>(ICommandAuditSink sink, TimeProvider clock)
    : ICommandBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    private readonly ICommandAuditSink _sink = sink;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(
        TCommand command,
        CommandPipelineStep<TResult> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(continuation);

        var startedAt = _clock.GetUtcNow();

        // Cố ý dùng hai đồng hồ khác nhau. GetUtcNow trả lời "lúc nào", và là giá trị auditor sẽ đọc.
        // GetTimestamp trả lời "mất bao lâu", và là monotonic — nó không nhảy khi NTP chỉnh lại đồng hồ
        // máy, nên duration không thể ra số âm.
        var startedTicks = _clock.GetTimestamp();

        try
        {
            var result = await continuation().ConfigureAwait(false);

            await WriteAsync(command, startedAt, startedTicks, failure: null, cancellationToken)
                .ConfigureAwait(false);

            return result;
        }
        catch (Exception failure)
        {
            // CancellationToken.None: thao tác đang bị bỏ dở, và đây chính là lúc audit trail đáng có
            // nhất. Truyền token đã bị cancel vào đây sẽ khiến việc ghi record cũng bị bỏ dở theo.
            await WriteAsync(command, startedAt, startedTicks, failure, CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
    }

    private Task WriteAsync(
        TCommand command,
        DateTimeOffset startedAt,
        long startedTicks,
        Exception? failure,
        CancellationToken cancellationToken) =>
        _sink.WriteAsync(
            new CommandAuditEntry(
                command.IdempotencyKey,
                typeof(TCommand).Name,
                startedAt,
                _clock.GetElapsedTime(startedTicks),
                failure is null,
                failure?.GetType().Name,
                failure?.Message),
            cancellationToken);
}
