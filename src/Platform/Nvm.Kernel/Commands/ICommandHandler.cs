namespace Nvm.Kernel.Commands;

/// <summary>
/// Đoạn code duy nhất biết cách thực hiện <typeparamref name="TCommand"/>.
/// </summary>
/// <typeparam name="TCommand">Command được xử lý.</typeparam>
/// <typeparam name="TResult">Kết quả trả về khi xử lý.</typeparam>
/// <remarks>
/// <para>
/// Một command, một handler. Đây là điều phân biệt command với event: một event có thể không được ai
/// consume hoặc được năm service consume, và publisher không biết cũng không quan tâm. Một command có
/// đúng một người nhận, và một command không ai xử lý là một lỗi chứ không phải một no-op — xem
/// <see cref="CommandHandlerNotFoundException"/>.
/// </para>
/// <para>
/// Handler chỉ chứa business rule và không gì khác. Validation, deduplication, auditing và
/// transaction scope là các pipeline behaviour bọc quanh lời gọi này, để chúng chỉ được viết một lần
/// thay vì phải nhớ cài lại bốn mươi lần. Sự kết hợp đó đến cùng với chính các behaviour.
/// </para>
/// <para>
/// Một handler phải an toàn khi chạy hai lần trên cùng một command (AGENTS.md K7). Idempotency
/// behaviour loại bỏ phần lớn các lần lặp lại, nhưng "phần lớn" không phải là một đảm bảo, và một
/// handler âm thầm giả định rằng nó chỉ chạy một lần là một handler sẽ đếm trùng đúng vào những điều
/// kiện mà không ai test tới.
/// </para>
/// </remarks>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>Thực hiện command.</summary>
    /// <param name="command">Command cần xử lý.</param>
    /// <param name="cancellationToken">Cancellation cho toàn bộ thao tác.</param>
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
