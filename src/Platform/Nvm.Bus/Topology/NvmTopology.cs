using System.Globalization;
using Nvm.Contracts.CloudEvents;

namespace Nvm.Bus.Topology;

/// <summary>
/// Các tên và pattern tạo nên Manufacturing Service Bus: một event đi tới exchange nào, và một
/// consumer nói ra thứ nó muốn bằng cách nào.
/// </summary>
/// <remarks>
/// <para>
/// Topology là một quyết định kiến trúc, không phải cấu hình. Một khi một service đã publish tới
/// <c>nvm.factory-model</c> và ba consumer đã bind vào đó, cái tên không thể đổi mà không phối hợp với
/// từng consumer một — nên nó được quyết định ở đây, một lần duy nhất, và không ai tự ráp một cái tên
/// bằng cách nối chuỗi tại nơi gọi.
/// </para>
/// <para>
/// Một exchange cho mỗi <b>bounded context</b> chứ không phải cho mỗi loại message. Một context là một
/// đơn vị ổn định có chủ sở hữu; một loại message thì không. Thêm event thứ tư vào Traceability không
/// nên thêm exchange thứ tư để mọi consumer phải khám phá ra.
/// </para>
/// </remarks>
public static class NvmTopology
{
    /// <summary>Tiền tố trên mọi exchange và queue mà hệ thống này khai báo.</summary>
    public const string Prefix = "nvm";

    /// <summary>Khớp đúng một segment của routing key.</summary>
    public const string OneSegment = "*";

    /// <summary>Khớp không hoặc nhiều segment ở cuối.</summary>
    public const string AnySegments = "#";

    private const char Separator = '.';

    /// <summary>Exchange mà một bounded context publish tới, ví dụ <c>nvm.factory-model</c>.</summary>
    public static string ExchangeFor(string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        return string.Concat(Prefix, Separator.ToString(), context);
    }

    /// <summary>Exchange mà một event type publish tới, đọc từ các contract attribute của nó.</summary>
    public static string ExchangeFor(EventTypeName eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return ExchangeFor(eventType.Context);
    }

    /// <summary>Mọi thứ xảy ra ở một nhà máy: <c>nvm.NV1.#</c>.</summary>
    /// <remarks>
    /// Subscription mà một service chỉ hoạt động trong một site mong muốn. Đa nhà máy (multiplant) tự
    /// nhiên có được từ routing key chứ không phải từ một filter trong mỗi consumer — một service ở
    /// Hải Phòng ngay từ đầu không bao giờ nhận được một message từ Leipzig, nên nó không thể để lộ
    /// message đó chỉ vì quên kiểm tra.
    /// </remarks>
    public static string BindingForSite(string siteId) =>
        Join(Prefix, RequireSite(siteId), AnySegments);

    /// <summary>Một context tại một nhà máy: <c>nvm.NV1.traceability.#</c>.</summary>
    public static string BindingForContext(string siteId, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        return Join(Prefix, RequireSite(siteId), context, AnySegments);
    }

    /// <summary>Một event, ở mọi nhà máy: <c>nvm.*.factory-model.revision-activated.v1</c>.</summary>
    /// <remarks>
    /// Là wildcard một-segment, không phải wildcard nhiều-segment. <c>nvm.#</c> cũng sẽ khớp, nhưng
    /// cũng sẽ gửi mọi event khác trong hệ thống tới một consumer chỉ yêu cầu một event.
    /// </remarks>
    public static string BindingForEventAtEverySite(EventTypeName eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return Join(Prefix, OneSegment, eventType.Context, eventType.Name, VersionSegment(eventType));
    }

    /// <summary>Một event tại một nhà máy. Binding hẹp nhất có thể có.</summary>
    public static string BindingForEvent(string siteId, EventTypeName eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return RoutingKey.Create(RequireSite(siteId), eventType).Value;
    }

    private static string VersionSegment(EventTypeName eventType) =>
        "v" + eventType.Version.ToString(CultureInfo.InvariantCulture);

    // Chữ hoa ở đây không phải để cho đẹp. AMQP so sánh routing key byte theo byte, nên một publisher
    // trên nvm.NV1.* và một consumer bind vào nvm.nv1.# không bao giờ gặp nhau — và broker không báo
    // lỗi gì cả. Từ chối cách viết chữ thường ngay tại nơi duy nhất binding được xây dựng chính là lúc
    // ai đó phát hiện ra vấn đề.
    private static string RequireSite(string siteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        return siteId.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character))
            ? siteId
            : throw new FormatException($"Site '{siteId}' must be upper-case letters and digits.");
    }

    private static string Join(params string[] segments) => string.Join(Separator, segments);
}
