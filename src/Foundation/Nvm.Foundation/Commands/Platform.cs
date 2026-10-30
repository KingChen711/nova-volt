namespace Nvm.Foundation;

/// <summary>Mức của một dòng trace. Đọc nội dung dòng, đừng chỉ lọc theo mức.</summary>
public enum TraceLevel
{
    Verbose = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
}

/// <summary>Ghi trace của handler; dòng mang <c>CorrelationId</c> của lời gọi đang chạy.</summary>
public interface ITracer
{
    void Write(TraceLevel level, string message);
}

/// <summary>Phần chỉ đọc của platform: thứ duy nhất pre-check được dùng.</summary>
public interface IReadOnlyPlatform
{
    /// <summary>Đọc trong transaction của command, chỉ entity cùng domain. Thấy cả thay đổi đã <c>Submit</c>.</summary>
    /// <exception cref="ModelMisuseException">Entity thuộc domain khác; dùng <see cref="ProjectionQuery{T}"/>.</exception>
    IQueryable<T> Query<T>()
        where T : ModelObject;

    /// <summary>Đọc ngoài transaction, mọi domain, chỉ bản đã commit. Không dùng để sửa.</summary>
    IQueryable<T> ProjectionQuery<T>()
        where T : ModelObject;

    ITracer Tracer { get; }

    TimeProvider Time { get; }

    /// <summary>Người gọi lời gọi gốc; role dùng cho state machine (role theo transition).</summary>
    CallerIdentity Caller { get; }
}

/// <summary>Platform mà handler và post-action nhận.</summary>
public interface IPlatform : IReadOnlyPlatform
{
    /// <summary>Đối tượng mới có <c>Id</c>; chưa vào transaction cho tới khi <see cref="Submit"/>.</summary>
    T Create<T>()
        where T : ModelObject, new();

    /// <summary>Đẩy thay đổi vào transaction để phần sau của chuỗi thấy. Commit do nền làm ở cuối chuỗi.</summary>
    /// <exception cref="ModelValidationException">Thiếu thuộc tính <see cref="MandatoryAttribute"/>.</exception>
    void Submit(ModelObject item);

    /// <summary>Xóa mềm: <c>IsDeleted</c> = số lớn nhất của bảng + 1.</summary>
    void Delete(ModelObject item);

    /// <summary>
    /// Gọi đồng bộ command khác. Cùng domain thì chung transaction; khác domain thì transaction riêng, commit ngay.
    /// Người gọi tự kiểm <see cref="CommandResponse.Succeeded"/>.
    /// </summary>
    TResponse CallCommand<TCommand, TResponse>(TCommand command)
        where TCommand : ICommand<TResponse>
        where TResponse : CommandResponse, new();

    /// <summary>Đăng ký event; chỉ phát nếu transaction commit thành công.</summary>
    void RaiseEventOnCommit(IFoundationEvent domainEvent);

    /// <summary>
    /// Endpoint của hệ ngoài theo tên (chuỗi kết nối, URL), lấy từ cấu hình của môi trường, không từ model. Null khi môi
    /// trường không khai: mỗi chỗ gọi tự xử lý.
    /// </summary>
    string? Endpoint(string name);

    /// <summary>
    /// Gửi tin đồng bộ: chờ hệ nhận trả lời tối đa <paramref name="timeout"/>. Gửi ngay lúc gọi, nằm ngoài transaction: command
    /// hỏng sau đó thì tin đã đi vẫn đã đi. Handler tự kiểm <see cref="MessageReply.Delivered"/> và nội dung trả lời.
    /// </summary>
    MessageReply SendMessage(OutboundMessage message, TimeSpan timeout);

    /// <summary>Gửi tin bất đồng bộ: không chờ, không có trả lời; lỗi gửi chỉ có dòng trace.</summary>
    void SendMessageAsync(OutboundMessage message);
}

/// <summary>Platform của composite command: không có transaction của riêng nó.</summary>
public interface ICompositePlatform
{
    /// <summary>Chạy command qua đủ pipeline của app, trong transaction riêng; lỗi ở lời gọi sau không hoàn tác lời gọi trước.</summary>
    TResponse CallCommand<TCommand, TResponse>(TCommand command)
        where TCommand : ICommand<TResponse>
        where TResponse : CommandResponse, new();

    IQueryable<T> ProjectionQuery<T>()
        where T : ModelObject;

    ITracer Tracer { get; }

    TimeProvider Time { get; }

    CallerIdentity Caller { get; }
}

/// <summary>Dùng sai model: đọc khác domain bằng <c>Query</c>, ghi trong pre-check… Runtime của luật mà NVM004 bắt lúc build.</summary>
public sealed class ModelMisuseException(string message) : InvalidOperationException(message);

/// <summary>Đối tượng vi phạm ràng buộc của model (thiếu thuộc tính bắt buộc).</summary>
public sealed class ModelValidationException(string message) : InvalidOperationException(message);
