using System.Text.Json.Serialization;

namespace Nvm.Foundation;

/// <summary>Command trả <typeparamref name="TResponse"/>.</summary>
public interface ICommand<TResponse>
    where TResponse : CommandResponse, new();

/// <summary>Lỗi do handler, pre-check hoặc post-action đặt bằng <c>SetError</c>.</summary>
public sealed record ExecutionError(int ErrorCode, string ErrorMessage);

/// <summary>Response của command: <c>{"Error":{"ErrorCode","ErrorMessage"},"Succeeded":false}</c> khi lỗi.</summary>
/// <remarks>
/// <see cref="Succeeded"/> chỉ nói handler chạy xong không báo lỗi. Với cấu hình
/// <see cref="Runtime.CommitFailureReporting.HandlerResult"/>, commit có thể hỏng sau đó (lab L02).
/// </remarks>
public class CommandResponse
{
    [JsonPropertyOrder(1)]
    public ExecutionError? Error { get; private set; }

    [JsonPropertyOrder(0)]
    public bool Succeeded => Error is null;

    /// <summary>Đánh dấu thất bại. Transaction của command sẽ rollback.</summary>
    public void SetError(int errorCode, string errorMessage) => Error = new ExecutionError(errorCode, errorMessage);
}

/// <summary>Kết quả của pre-check; <c>SetError</c> là chặn command.</summary>
public sealed class PreCheckResponse
{
    public ExecutionError? Error { get; private set; }

    public bool Succeeded => Error is null;

    public void SetError(int errorCode, string errorMessage) => Error = new ExecutionError(errorCode, errorMessage);
}

/// <summary>Handler của command trong functional block. Đồng bộ, như handler của nền thật.</summary>
public interface ICommandHandler<in TCommand, out TResponse>
    where TCommand : ICommand<TResponse>
    where TResponse : CommandResponse, new()
{
    TResponse Handle(TCommand command, IPlatform platform);
}

/// <summary>Handler của composite command của app: mỗi <c>CallCommand</c> chạy một transaction riêng.</summary>
public interface ICompositeCommandHandler<in TCommand, out TResponse>
    where TCommand : ICommand<TResponse>
    where TResponse : CommandResponse, new()
{
    TResponse Handle(TCommand command, ICompositePlatform platform);
}

/// <summary>Pre-check gắn vào <typeparamref name="TCommand"/>: chỉ đọc, chạy trước handler.</summary>
public interface IPreCheck<in TCommand>
{
    PreCheckResponse Check(TCommand command, IReadOnlyPlatform platform);
}

/// <summary>Post-action gắn vào <typeparamref name="TCommand"/>: đọc và ghi, chạy sau handler trong cùng transaction.</summary>
public interface IPostAction<in TCommand, TResponse>
    where TResponse : CommandResponse, new()
{
    TResponse Execute(TCommand command, TResponse response, IPlatform platform);
}

/// <summary>Event phát sau commit. Lớp cụ thể phải có <see cref="EventAttribute{TBlock}"/> và <see cref="EventVersionAttribute"/>.</summary>
public interface IFoundationEvent;
