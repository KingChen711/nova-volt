namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Tên các transport header mang theo các attribute CloudEvents cùng với một message.
/// </summary>
/// <remarks>
/// <para>
/// CloudEvents định nghĩa binding cho HTTP (header tiền tố <c>ce-</c>), Kafka (<c>ce_</c>), AMQP
/// <b>1.0</b> (application property tiền tố <c>cloudEvents:</c>), MQTT và NATS. RabbitMQ nói AMQP
/// <b>0-9-1</b>, thứ không nằm trong danh sách đó — nên không có binding chính thức nào để theo, và
/// bất kỳ lựa chọn nào ở đây đều là một quy ước cục bộ.
/// </para>
/// <para>
/// Tiền tố <c>ce_</c> được mượn từ binding Kafka, cái gần giống nhất về hình dạng. Được ghi rõ ra ở
/// đây thay vì mặc định ngầm hiểu, để ba năm sau không ai đi tìm một đặc tả nói ra điều này. Xem
/// <c>ADR-008</c>.
/// </para>
/// <para>
/// Đây là những attribute có thể suy ra được từ chính event. Các attribute tùy chọn —
/// <c>subject</c>, <c>dataschema</c>, <c>correlationid</c>, <c>causationid</c>,
/// <c>partitionkey</c> — cần một publisher cung cấp chúng và bị <b>bỏ qua</b> thay vì ghi rỗng:
/// CloudEvents coi một attribute vắng mặt và một attribute null là hai khẳng định khác nhau.
/// </para>
/// </remarks>
public static class CloudEventHeaders
{
    /// <summary>Tiền tố chung cho mọi header attribute CloudEvents.</summary>
    public const string Prefix = "ce_";

    /// <summary>Phiên bản đặc tả. Luôn là <c>1.0</c>.</summary>
    public const string SpecVersion = Prefix + "specversion";

    /// <summary>Danh tính event. Bằng với <c>EventId</c> của event, và với idempotency key của command.</summary>
    public const string Id = Prefix + "id";

    /// <summary>Chuyện gì đã xảy ra: <c>com.novavolt.{context}.{event}.v{n}</c>.</summary>
    public const string Type = Prefix + "type";

    /// <summary>Ai nói vậy: <c>urn:novavolt:{site}:{application}</c>.</summary>
    public const string Source = Prefix + "source";

    /// <summary>Khi nào hệ thống ghi nhận sự việc, RFC 3339 với offset tường minh.</summary>
    public const string Time = Prefix + "time";

    /// <summary>Kiểu mã hóa của payload.</summary>
    public const string DataContentType = Prefix + "datacontenttype";
}
