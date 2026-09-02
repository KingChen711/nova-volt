using System.Globalization;
using MassTransit;
using Nvm.Contracts.CloudEvents;

namespace Nvm.Bus.CloudEvents;

/// <summary>Các thuộc tính CloudEvents đọc lại từ một message nhận được.</summary>
/// <param name="SpecVersion">Phiên bản đặc tả mà publisher đã dùng.</param>
/// <param name="Id">Định danh event — giá trị mà deduplication dùng làm khóa.</param>
/// <param name="Type">Chuyện gì đã xảy ra, và schema version nào nói lên điều đó.</param>
/// <param name="Source">Deployable nào, ở nhà máy nào, đã khẳng định điều này.</param>
/// <param name="Time">Khi nào publisher ghi nhận sự kiện này.</param>
/// <param name="DataContentType">
/// Payload được mã hóa theo kiểu gì, ví dụ <c>application/json</c>.
/// </param>
/// <remarks>
/// Cả sáu thuộc tính đều bắt buộc và được đọc như một tập hợp duy nhất, vì message mà type này hữu ích
/// nhất là message mà không ai deserialize được — đang nằm trong một queue <c>_error</c> trong lúc ai
/// đó cố tìm hiểu nó là gì. Nếu chỉ báo cáo năm thuộc tính kia và bỏ qua encoding thì sẽ khiến người đọc
/// mặc định là JSON — mà chính cái giả định đó đã đưa message vào đây ngay từ đầu.
/// </remarks>
public sealed record CloudEventAttributes(
    string SpecVersion,
    Guid Id,
    EventTypeName Type,
    EventSource Source,
    DateTimeOffset Time,
    string DataContentType);

/// <summary>Đọc các thuộc tính CloudEvents từ một message đã consume.</summary>
/// <remarks>
/// <para>
/// Đây là một extension trên consume context chứ không phải một filter đổ vào một scoped service. Một
/// filter sẽ phải chạy cho mọi message dù có ai xem thuộc tính hay không, và sẽ thêm một registration
/// phải luôn đồng bộ với phía send. Đọc theo yêu cầu (on demand) làm đúng việc đó mà không có gì cần giữ
/// đồng bộ cả.
/// </para>
/// <para>
/// Một message <b>không mang</b> header <c>ce_</c> nào cả không phải là lỗi. Không phải mọi thứ trên bus
/// đều đi qua publish path của hệ thống này, và MassTransit đã route và deserialize message đó bằng
/// envelope riêng của nó; từ chối nó ở đây sẽ là từ chối một message mà hệ thống đã hiểu rồi. Thuộc tính
/// vắng mặt là một dữ kiện về message, nên chúng được báo cáo là <see langword="null"/>.
/// </para>
/// <para>
/// Một tập hợp <b>không đầy đủ</b> lại là chuyện khác, và bị từ chối. Sáu thuộc tính được ghi cùng nhau
/// bởi một filter duy nhất, nên có từ một đến năm thuộc tính nghĩa là message đã được đóng dấu bởi thứ gì
/// đó không thống nhất với hệ thống này về việc tập hợp đó gồm những gì. Chỉ báo cáo những cái đang có sẽ
/// khiến người đọc mặc định phần còn lại — và giá trị mặc định cho encoding bị thiếu chính là giả định
/// JSON mà envelope này tồn tại để ngăn chặn.
/// </para>
/// </remarks>
public static class CloudEventContextExtensions
{
    private const int MandatoryHeaderCount = 6;

    /// <summary>Đọc các thuộc tính CloudEvents, hoặc null khi message không mang thuộc tính nào.</summary>
    /// <param name="context">Consume context.</param>
    /// <returns>
    /// Sáu thuộc tính khi cả sáu header đều có mặt; <see langword="null"/> khi không header nào có mặt
    /// cả.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Message mang một số header bắt buộc nhưng không đủ cả sáu.
    /// </exception>
    public static CloudEventAttributes? CloudEvent(this ConsumeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Cả sáu đều được đọc trước khi bất kỳ cái nào bị đánh giá. Chọn một cái làm sentinel — specversion
        // là lựa chọn hấp dẫn — sẽ khiến riêng header đó quyết định giữa "không có thuộc tính nào" và "một
        // tập hợp bị hỏng", nên một message chỉ thiếu mỗi sentinel sẽ bị đọc thành message không mang gì cả.
        var specVersion = Read(context, CloudEventHeaders.SpecVersion);
        var id = Read(context, CloudEventHeaders.Id);
        var type = Read(context, CloudEventHeaders.Type);
        var source = Read(context, CloudEventHeaders.Source);
        var time = Read(context, CloudEventHeaders.Time);
        var dataContentType = Read(context, CloudEventHeaders.DataContentType);

        if (specVersion is null
            && id is null
            && type is null
            && source is null
            && time is null
            && dataContentType is null)
        {
            return null;
        }

        if (specVersion is null
            || id is null
            || type is null
            || source is null
            || time is null
            || dataContentType is null)
        {
            throw PartialSet(
                (CloudEventHeaders.SpecVersion, specVersion),
                (CloudEventHeaders.Id, id),
                (CloudEventHeaders.Type, type),
                (CloudEventHeaders.Source, source),
                (CloudEventHeaders.Time, time),
                (CloudEventHeaders.DataContentType, dataContentType));
        }

        return new CloudEventAttributes(
            specVersion,
            Guid.Parse(id, CultureInfo.InvariantCulture),
            EventTypeName.Parse(type),
            EventSource.Parse(source),
            DateTimeOffset.Parse(time, CultureInfo.InvariantCulture),
            dataContentType);
    }

    // Có mặt nhưng không đọc được không giống với vắng mặt, và `value as string` lại gộp hai trường hợp
    // này làm một: một header mang sai kiểu sẽ trả về null và message sẽ bị đọc như thể chưa từng có
    // thuộc tính đó. Đó là lỗi giống hệt vấn đề sentinel specversion ở lớp bên dưới — reader báo cáo
    // "không có gì ở đây" trong khi header vẫn đang nằm trên message nói điều ngược lại.
    private static string? Read(ConsumeContext context, string header)
    {
        if (!context.Headers.TryGetHeader(header, out var value) || value is null)
        {
            return null;
        }

        if (value is not string text)
        {
            throw new InvalidOperationException(
                $"Header '{header}' is present but carries {value.GetType()} rather than a string. "
                + "CloudEvents attributes travel as text; this is malformed metadata, not absent "
                + "metadata.");
        }

        // CloudEvents quy định rằng một thuộc tính vắng mặt và một thuộc tính có mặt nhưng rỗng là hai
        // khẳng định khác nhau, đó là lý do năm thuộc tính tùy chọn bị bỏ qua thay vì ghi rỗng (ADR-008).
        // Đọc ngược lại cùng quy tắc đó: một header bắt buộc mà rỗng nghĩa là publisher đang khẳng định
        // encoding là "" — sai định dạng — chứ không phải publisher im lặng không nói gì.
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"Header '{header}' is present but empty. An absent CloudEvents attribute and an "
                + "empty one are different statements; an empty mandatory one is malformed.");
        }

        return text;
    }

    // Một tập hợp thuộc tính chỉ có một nửa còn tệ hơn là không có gì cả: reader sẽ lấy những cái đang có
    // rồi âm thầm mặc định phần còn lại. Hoặc publisher đã đóng dấu message, hoặc không.
    //
    // Thông báo nêu tên mọi header đang thiếu, không chỉ cái đầu tiên tìm thấy. Người đọc thứ này từ một
    // message trong queue _error đang cố tìm ra publisher nào đã tạo ra nó, và "ba trong sáu cái đang
    // thiếu" thu hẹp phạm vi tốt hơn nhiều so với "cái đầu tiên đang thiếu".
    private static InvalidOperationException PartialSet(
        params (string Header, string? Value)[] attributes)
    {
        var missing = attributes.Where(pair => pair.Value is null).Select(pair => pair.Header);

        return new InvalidOperationException(
            $"Message carries only {attributes.Count(pair => pair.Value is not null)} of the "
            + $"{MandatoryHeaderCount} mandatory CloudEvents headers; missing: "
            + $"{string.Join(", ", missing)}. CloudEvents attributes are written as a set.");
    }
}
