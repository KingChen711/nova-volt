namespace Nvm.Kernel.Commands;

/// <summary>
/// Một yêu cầu thay đổi điều gì đó, gửi đến đúng một handler.
/// </summary>
/// <remarks>
/// <para>
/// Là đối trọng của một domain event. Một event là một sự việc đã xảy ra, được phát biểu ở thì quá
/// khứ; một command là một ý định (intention) vẫn có thể bị từ chối, và được đặt tên ở thể mệnh lệnh:
/// <c>ActivateFactoryModelRevision</c>, <c>QuarantineUnit</c>, <c>ApproveRecipeVersion</c>.
/// </para>
/// <para>
/// Interface non-generic này tồn tại để pipeline có thể đọc <see cref="IdempotencyKey"/> từ một
/// command mà không cần biết command đó trả về gì. Các implementation dùng <see cref="ICommand{TResult}"/>.
/// </para>
/// </remarks>
public interface ICommand
{
    /// <summary>
    /// Định danh cho chính ý định đó, để nhận nó hai lần không thực hiện nó hai lần.
    /// </summary>
    /// <remarks>
    /// Bắt buộc chứ không phải tuỳ chọn (AGENTS.md K7). Một khoá tuỳ chọn sẽ bị bỏ quên đúng ở chỗ
    /// quan trọng nhất — trong đường retry, do người đang vội viết ra.
    /// </remarks>
    IdempotencyKey IdempotencyKey { get; }
}

/// <summary>Một command mà handler của nó tạo ra <typeparamref name="TResult"/>.</summary>
/// <typeparam name="TResult">Kết quả khi xử lý command, thường là event mà nó gây ra.</typeparam>
/// <remarks>
/// <para>
/// Cố ý không có biến thể trả về void. Trong domain này, một command làm thay đổi bất cứ điều gì đều
/// tạo ra ít nhất là event ghi lại thay đổi đó, và caller cần event đó — để publish nó, để trả nó về,
/// hoặc để ghi nó vào store. Một handler không có gì để trả lại thường là một handler đã quên ghi lại
/// việc nó đã làm.
/// </para>
/// <para>
/// Giữ một hình dạng duy nhất cũng giữ một đường dispatch duy nhất. Có hai hình dạng sẽ nhân đôi phần
/// generic machinery trong <see cref="ICommandDispatcher"/> mà chưa ai từng cần đến lợi ích đó.
/// </para>
/// </remarks>
public interface ICommand<out TResult> : ICommand;
