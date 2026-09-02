namespace Nvm.Kernel.Commands;

/// <summary>Được ném khi một command được dispatch mà không có handler nào đăng ký cho nó.</summary>
/// <remarks>
/// Luôn là lỗi wiring, không bao giờ là một tình huống lúc chạy: hoặc assembly của handler chưa được
/// truyền vào kernel registration, hoặc handler không thực sự implement interface mà nó trông giống
/// như đang implement. Message nêu tên command type vì đó là thứ duy nhất người đọc log có để dựa vào.
/// </remarks>
public sealed class CommandHandlerNotFoundException : InvalidOperationException
{
    /// <summary>Tạo exception cho một command type không có handler.</summary>
    /// <param name="commandType">Command không thể dispatch được.</param>
    public CommandHandlerNotFoundException(Type commandType)
        : base(BuildMessage(commandType)) => CommandType = commandType;

    /// <summary>Tạo exception với một message tuỳ chỉnh.</summary>
    public CommandHandlerNotFoundException(string message)
        : base(message) => CommandType = typeof(void);

    /// <summary>Tạo exception với một message tuỳ chỉnh và inner exception.</summary>
    public CommandHandlerNotFoundException(string message, Exception innerException)
        : base(message, innerException) => CommandType = typeof(void);

    /// <summary>Command type không có handler.</summary>
    public Type CommandType { get; }

    private static string BuildMessage(Type commandType)
    {
        ArgumentNullException.ThrowIfNull(commandType);

        return $"No handler is registered for command '{commandType.FullName}'. "
            + "Register one by passing its assembly to AddNvmKernel.";
    }
}
