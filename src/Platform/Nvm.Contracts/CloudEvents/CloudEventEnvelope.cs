using System.Text.Json.Serialization;
using Nvm.Contracts.Events;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Một domain event được bọc trong một envelope CloudEvents 1.0, như định nghĩa ở docs/scope.md §7.4.
/// </summary>
/// <typeparam name="TData">Domain event được mang trong <see cref="Data"/>.</typeparam>
/// <remarks>
/// <para>
/// Envelope này là hình dạng chuẩn (canonical) của một event: đó là thứ event store lưu trữ, thứ một
/// passport được publish mang theo, và thứ một auditor cuối cùng được xem. Nó cố ý không giống với bất
/// cứ thứ gì mà một message library bọc quanh một payload để tự làm routing và retry — cái đó thuộc về
/// transport và có thể bị thay thế; còn hình dạng này thì không được phép.
/// </para>
/// <para>
/// Hai quy ước đặt tên gặp nhau trong cùng một document, và cả hai đều đúng. Tên attribute CloudEvents
/// là chữ thường không có dấu phân cách (<c>specversion</c>, <c>datacontenttype</c>) vì đặc tả yêu cầu
/// vậy. Các field bên trong <see cref="Data"/> là camelCase vì đó là quy ước hệ thống này chọn cho
/// payload của riêng nó. Các property được khai báo bên dưới theo đúng thứ tự trên wire để một envelope
/// đã serialize đọc giống với ví dụ ở §7.4.
/// </para>
/// </remarks>
public sealed record CloudEventEnvelope<TData>
    where TData : IDomainEvent
{
    /// <summary>Phiên bản đặc tả CloudEvents duy nhất mà hệ thống này phát ra.</summary>
    public const string SpecVersionValue = "1.0";

    /// <summary>Kiểu mã hóa payload duy nhất mà envelope này mang.</summary>
    /// <remarks>
    /// Sparkplug B đến dưới dạng protobuf, nhưng nó được giải mã ở edge và chỉ trở thành một domain
    /// event sau đó (M2). Không gì đến được envelope này mà không phải là JSON.
    /// </remarks>
    public const string JsonContentType = "application/json";

    /// <summary>CloudEvents <c>specversion</c>. Là hằng số theo thiết kế.</summary>
    [JsonPropertyName("specversion")]
    [JsonPropertyOrder(0)]
    public string SpecVersion => SpecVersionValue;

    /// <summary>
    /// CloudEvents <c>id</c>. Đọc thẳng từ payload thay vì lưu riêng.
    /// </summary>
    /// <remarks>
    /// Hai nơi cùng giữ một danh tính là hai nơi có thể bất đồng, và thời điểm duy nhất điều đó quan
    /// trọng là lúc deduplication — khi một sự lệch nhau nghĩa là cùng một sự việc bị đếm hai lần. Suy
    /// ra giá trị này thay vì lưu riêng khiến sự bất đồng đó không thể tồn tại được, chứ không chỉ là
    /// bị hạn chế.
    /// </remarks>
    [JsonPropertyName("id")]
    [JsonPropertyOrder(1)]
    public Guid Id => Data.EventId;

    /// <summary>Chuyện gì đã xảy ra, và schema version nào nói lên điều đó.</summary>
    [JsonPropertyName("type")]
    [JsonPropertyOrder(2)]
    public required EventTypeName Type { get; init; }

    /// <summary>Deployable nào, ở site nào, đang khẳng định điều này.</summary>
    [JsonPropertyName("source")]
    [JsonPropertyOrder(3)]
    public required EventSource Source { get; init; }

    /// <summary>
    /// Event này nói về cái gì, dưới dạng một URN — ví dụ
    /// <c>urn:trace-unit:cell:NV1CL16238A00123</c>.
    /// </summary>
    /// <remarks>
    /// Là tùy chọn trong đặc tả và cố ý để dưới dạng string ở đây. Xây dựng nó cần biết về số serial và
    /// loại unit, những thứ nằm ở tầng cao hơn một bậc; dạy project này biết về chúng sẽ khiến mũi tên
    /// dependency chỉ ngược lại.
    /// </remarks>
    [JsonPropertyName("subject")]
    [JsonPropertyOrder(4)]
    public string? Subject { get; init; }

    /// <summary>CloudEvents <c>time</c>. Suy ra từ payload, cùng lý do như <see cref="Id"/>.</summary>
    [JsonPropertyName("time")]
    [JsonPropertyOrder(5)]
    public DateTimeOffset Time => Data.OccurredAt;

    /// <summary>CloudEvents <c>datacontenttype</c>.</summary>
    [JsonPropertyName("datacontenttype")]
    [JsonPropertyOrder(6)]
    public string DataContentType => JsonContentType;

    /// <summary>Schema cho <see cref="Data"/> được publish ở đâu, khi nào nó được publish.</summary>
    [JsonPropertyName("dataschema")]
    [JsonPropertyOrder(7)]
    public Uri? DataSchema { get; init; }

    /// <summary>
    /// Luồng nghiệp vụ mà event này thuộc về, thường là một work order như <c>WO-2026-0042</c>.
    /// </summary>
    /// <remarks>
    /// Được chia sẻ bởi mọi event trong cùng một unit of work, đó là điều khiến việc hỏi "cho tôi xem
    /// mọi thứ đã xảy ra với đơn hàng này" trở nên khả thi qua các service không bao giờ gọi lẫn nhau.
    /// </remarks>
    [JsonPropertyName("correlationid")]
    [JsonPropertyOrder(8)]
    public string? CorrelationId { get; init; }

    /// <summary>Cái gì trực tiếp gây ra event này, thường là lần chạy thao tác hoặc command.</summary>
    /// <remarks>
    /// Correlation gom nhóm; causation sắp thứ tự. Kết hợp lại chúng dựng lại chuỗi dẫn tới đây — câu
    /// hỏi mà một cuộc điều tra thực sự đặt ra sau khi tìm thấy một lỗi (defect).
    /// </remarks>
    [JsonPropertyName("causationid")]
    [JsonPropertyOrder(9)]
    public string? CausationId { get; init; }

    /// <summary>
    /// Giá trị mà các event phải giữ đúng thứ tự tương đối với nhau, thường là unit id.
    /// </summary>
    /// <remarks>
    /// Thứ tự chỉ được đảm bảo trong phạm vi một partition key, không bao giờ toàn cục. Hai event về
    /// cùng một cell không được vượt mặt nhau; hai event về hai cell khác nhau thì có thể, và giả vờ
    /// như không phải vậy chính là điều biến một consumer song song thành một consumer tuần tự.
    /// </remarks>
    [JsonPropertyName("partitionkey")]
    [JsonPropertyOrder(10)]
    public string? PartitionKey { get; init; }

    /// <summary>Chính domain event đó — attribute <c>data</c>.</summary>
    [JsonPropertyName("data")]
    [JsonPropertyOrder(11)]
    public required TData Data { get; init; }
}
