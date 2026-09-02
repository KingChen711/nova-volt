namespace Nvm.Kernel.Commands;

/// <summary>Gọi stage tiếp theo của pipeline, kết thúc tại chính handler.</summary>
/// <typeparam name="TResult">Kết quả command trả về.</typeparam>
public delegate Task<TResult> CommandPipelineStep<TResult>();

/// <summary>
/// Một mối quan tâm (concern) bọc quanh mọi command thay vì phải nhớ cài lại trong từng handler.
/// </summary>
/// <typeparam name="TCommand">Command đang được xử lý.</typeparam>
/// <typeparam name="TResult">Kết quả trả về khi xử lý.</typeparam>
/// <remarks>
/// <para>
/// Validation, deduplication và auditing áp dụng cho khoảng bốn mươi command mà hệ thống này sẽ có.
/// Nếu để mặc cho từng handler tự làm, chúng sẽ được viết ba mươi chín lần và bị quên mất một lần — và
/// đúng chỗ bị quên là nơi defect nằm. Ở đây chúng chỉ được viết một lần và không thể bị bỏ qua.
/// </para>
/// <para>
/// Thứ tự là một tính chất về tính đúng đắn (correctness), không phải sở thích. Các behaviour chạy
/// theo thứ tự đăng ký, ngoài cùng chạy trước, và mỗi behaviour tự quyết định có gọi tiếp continuation
/// hay không. Xem <see cref="KernelServiceCollectionExtensions.AddNvmKernel"/> để biết thứ tự và vì
/// sao lại là thứ tự đó.
/// </para>
/// </remarks>
public interface ICommandBehavior<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>Chạy stage này, gọi <paramref name="continuation"/> để tiếp tục.</summary>
    /// <param name="command">Command đang đi qua pipeline.</param>
    /// <param name="continuation">Phần còn lại của pipeline. Không gọi nó nghĩa là dừng command tại đây.</param>
    /// <param name="cancellationToken">Cancellation cho toàn bộ thao tác.</param>
    Task<TResult> HandleAsync(
        TCommand command,
        CommandPipelineStep<TResult> continuation,
        CancellationToken cancellationToken);
}
