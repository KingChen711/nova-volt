namespace Nvm.Kernel.Commands.Validation;

/// <summary>Chặn một command sai định dạng ngay từ cửa, trước khi bất cứ thứ gì khác trong pipeline chạy.</summary>
/// <typeparam name="TCommand">Command đang được xử lý.</typeparam>
/// <typeparam name="TResult">Kết quả trả về khi xử lý.</typeparam>
/// <param name="validators">
/// Mọi validator đã đăng ký cho command này. Thường là không có hoặc chỉ có một; cho phép nhiều hơn một
/// để một Functional Block có thể thêm một rule vào một command mà nó không sở hữu.
/// </param>
/// <remarks>
/// <para>
/// Nằm ngoài cùng, để một command sai định dạng bị từ chối chỉ bằng chính nó — không cần đồng hồ,
/// không cần store, không cần round trip. Rẻ trước là lý do thông thường; lý do sắc hơn xuất hiện cùng
/// với deduplication store thật, nơi sẽ phải claim một khoá ngay khi đi vào để ngăn hai bản sao của
/// cùng một command chạy đồng thời. Một khi đã claim, bất cứ thứ gì đến được đó đều đánh dấu khoá của
/// nó là đã bị chiếm, và bản gửi lại đã sửa đúng của một command bị từ chối — cùng natural key, cùng
/// idempotency key — sẽ bị nuốt mất như một bản duplicate.
/// </para>
/// <para>
/// Tình huống đó là thật chứ không phải lý thuyết: khoá được suy ra từ natural key, nên một sai sót ở
/// bất kỳ field nào nằm ngoài natural key sẽ tạo ra một command khác nhưng mang cùng khoá. Xem
/// <see cref="KernelServiceCollectionExtensions.AddNvmKernel"/> để có lập luận đầy đủ về thứ tự này.
/// </para>
/// </remarks>
public sealed class ValidationBehavior<TCommand, TResult>(IEnumerable<ICommandValidator<TCommand>> validators)
    : ICommandBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    private readonly IEnumerable<ICommandValidator<TCommand>> _validators = validators;

    /// <inheritdoc />
    public Task<TResult> HandleAsync(
        TCommand command,
        CommandPipelineStep<TResult> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(continuation);

        // Mọi validator đều chạy, và mọi lỗi đều được thu thập. Dừng lại ở lỗi đầu tiên biến việc sửa
        // một form thành một trò đoán mò chơi từng vòng một.
        var failures = _validators
            .SelectMany(validator => validator.Validate(command))
            .ToList();

        return failures.Count == 0
            ? continuation()
            : throw new CommandValidationException(typeof(TCommand), failures);
    }
}
