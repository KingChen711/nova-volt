namespace Nvm.Kernel.Commands.Idempotency;

/// <summary>Identity đã được server xác thực; payload chuẩn hoá chỉ chứa các trường của ý định gửi.</summary>
public interface IDurableCommand : ICommand
{
    /// <summary>Tên contract ổn định, không phụ thuộc tên class hay namespace khi refactor.</summary>
    string CommandType { get; }
    /// <summary>Site lấy từ principal, không lấy từ dữ liệu do client tự khai.</summary>
    string SiteId { get; }
    /// <summary>Actor lấy từ principal đã xác thực.</summary>
    string ActorId { get; }
    /// <summary>Định danh giữ nguyên khi gửi lại.</summary>
    string SubmissionId { get; }
    /// <summary>Biểu diễn tất định của payload; không chứa timestamp nhận request hay correlation mới.</summary>
    string CanonicalPayload { get; }
}

/// <summary>Store cần identity của command trước khi claim; giữ tương thích API Claim/Complete/Abandon.</summary>
public interface IContextualIdempotencyStore : IIdempotencyStore
{
    /// <summary>Gắn identity cho một lần dispatch; phải từ chối dispatch lồng nhau trong cùng scope.</summary>
    void Prepare(ICommand command);
}
