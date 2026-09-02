using System.Globalization;

namespace Nvm.Kernel.Commands.Validation;

/// <summary>Được ném ra khi một command bị từ chối trước khi bất cứ điều gì tác động lên nó.</summary>
/// <remarks>
/// Mang theo mọi lỗi tìm được chứ không chỉ lỗi đầu tiên. Một người vận hành sửa một field, gửi lại,
/// rồi bị báo về field tiếp theo sẽ hết tin tưởng vào màn hình đó ngay từ vòng thứ ba.
/// </remarks>
public sealed class CommandValidationException : Exception
{
    /// <summary>Tạo exception từ các lỗi tìm thấy trên một command.</summary>
    /// <param name="commandType">Command bị từ chối.</param>
    /// <param name="failures">Mọi thứ sai trên command đó.</param>
    public CommandValidationException(Type commandType, IReadOnlyList<ValidationFailure> failures)
        : base(BuildMessage(commandType, failures))
    {
        CommandType = commandType;
        Failures = failures;
    }

    /// <summary>Tạo exception với một message tuỳ chỉnh.</summary>
    public CommandValidationException(string message)
        : base(message)
    {
        CommandType = typeof(void);
        Failures = [];
    }

    /// <summary>Tạo exception với một message tuỳ chỉnh và một inner exception.</summary>
    public CommandValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        CommandType = typeof(void);
        Failures = [];
    }

    /// <summary>Command bị từ chối.</summary>
    public Type CommandType { get; }

    /// <summary>Mọi thứ sai trên command đó.</summary>
    public IReadOnlyList<ValidationFailure> Failures { get; }

    private static string BuildMessage(Type commandType, IReadOnlyList<ValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(commandType);
        ArgumentNullException.ThrowIfNull(failures);

        var detail = string.Join("; ", failures.Select(failure => $"{failure.Field}: {failure.Message}"));

        return string.Format(
            CultureInfo.InvariantCulture,
            "Command '{0}' was rejected by validation ({1} problem(s)): {2}",
            commandType.Name,
            failures.Count,
            detail);
    }
}
