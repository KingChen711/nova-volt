namespace Nvm.Foundation;

/// <summary>
/// Tin gửi ra hệ ngoài. <see cref="Adapter"/> và <see cref="MessageType"/> quyết định tin đi đâu; ánh xạ adapter tới hệ nhận là
/// cấu hình của tầng tích hợp, không nằm trong handler.
/// </summary>
public sealed record OutboundMessage(string Adapter, string MessageType, string Content, string ContentType = "application/json");

/// <summary>Kết quả của một lần gửi đồng bộ: hệ nhận có trả lời không, và trả gì.</summary>
public sealed class MessageReply
{
    public bool Delivered { get; init; }

    public string? Content { get; init; }

    /// <summary>Lý do không gửi được (không tới được gateway, hết thời gian chờ, hệ nhận báo lỗi).</summary>
    public string? Error { get; init; }

    public static MessageReply Failed(string error) => new() { Error = error };

    public static MessageReply Of(string? content) => new() { Delivered = true, Content = content };
}

/// <summary>Đường ra của tin: Foundation gọi, host hoặc test cài.</summary>
public interface IMessageGateway
{
    /// <summary>Gửi và chờ trả lời tối đa <paramref name="timeout"/>. Không ném lỗi: lỗi nằm trong <see cref="MessageReply.Error"/>.</summary>
    MessageReply Send(OutboundMessage message, TimeSpan timeout);

    /// <summary>Gửi rồi đi tiếp, không có trả lời. Lỗi chỉ ở trace của bên gửi.</summary>
    void Post(OutboundMessage message);
}
