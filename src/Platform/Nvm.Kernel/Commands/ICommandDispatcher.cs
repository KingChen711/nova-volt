namespace Nvm.Kernel.Commands;

/// <summary>Gửi một command tới handler của nó.</summary>
/// <remarks>
/// Đường nối (seam) cho phép mọi thứ ở giữa được thêm vào sau này mà không đụng tới cả hai phía. Một
/// caller yêu cầu một command được thực hiện; nó không biết class nào làm việc đó, và cũng không biết
/// pipeline đặt thêm gì quanh nó.
/// </remarks>
public interface ICommandDispatcher
{
    /// <summary>Dispatch một command và trả về kết quả handler của nó tạo ra.</summary>
    /// <typeparam name="TResult">Kết quả command trả về.</typeparam>
    /// <param name="command">Command cần thực hiện.</param>
    /// <param name="cancellationToken">Cancellation cho toàn bộ thao tác.</param>
    /// <exception cref="CommandHandlerNotFoundException">Không có handler nào đăng ký cho command này.</exception>
    Task<TResult> DispatchAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
}
