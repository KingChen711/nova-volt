namespace Nvm.Kernel.Commands.Audit;

/// <summary>Một dòng của audit trail: một command đã được thử thực hiện, và đây là kết quả.</summary>
/// <param name="IdempotencyKey">Đây là ý định (intention) nào, và là mối nối ngược về event mà nó đã sinh ra.</param>
/// <param name="CommandType">Tên của command.</param>
/// <param name="StartedAt">Lúc bắt đầu xử lý, lấy từ đồng hồ hệ thống (ambient clock).</param>
/// <param name="Duration">Mất bao lâu. Một bước bình thường mất 40 ms mà giờ mất 4 s là một dấu hiệu bất thường.</param>
/// <param name="Succeeded">Handler có hoàn thành hay không.</param>
/// <param name="FailureType">Tên của exception khi thất bại, ngược lại là null.</param>
/// <param name="FailureMessage">Vì sao thất bại, ngược lại là null.</param>
public sealed record CommandAuditEntry(
    IdempotencyKey IdempotencyKey,
    string CommandType,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    bool Succeeded,
    string? FailureType,
    string? FailureMessage);

/// <summary>Nơi các audit entry được ghi tới.</summary>
/// <remarks>
/// <para>
/// Audit trail không phải log. Log là cho người đang debug hôm nay và có thể bị sample, rotate hay tắt
/// đi; audit trail là hồ sơ về những gì nhà máy đã làm, được auditor đọc nhiều năm sau, và IATF 16949
/// yêu cầu nó vẫn phải còn đó. Cùng dùng từ giống nhau nhưng nghĩa vụ khác nhau — đó là lý do đây là
/// một abstraction riêng chứ không phải một lời gọi logger.
/// </para>
/// <para>
/// <b>Khoảng trống đã biết:</b> một entry chưa có actor (người thực hiện), vì command hiện chưa mang
/// thông tin đó. "Ai đã release lot này" là câu hỏi mà trail hiện chưa trả lời được, và đó đúng là câu
/// hỏi mà một cuộc audit sẽ hỏi. Identity sẽ đến backend cùng với chữ ký điện tử, và lúc đó entry sẽ có
/// thêm field cho việc này.
/// </para>
/// </remarks>
public interface ICommandAuditSink
{
    /// <summary>Ghi lại một lần thử.</summary>
    /// <param name="entry">Điều gì đã xảy ra.</param>
    /// <param name="cancellationToken">Cancellation cho toàn bộ thao tác.</param>
    Task WriteAsync(CommandAuditEntry entry, CancellationToken cancellationToken);
}
