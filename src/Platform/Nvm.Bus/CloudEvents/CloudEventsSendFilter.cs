using System.Globalization;
using MassTransit;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;

namespace Nvm.Bus.CloudEvents;

/// <summary>Đóng dấu mọi event gửi đi với các thuộc tính CloudEvents của nó dưới dạng transport header.</summary>
/// <typeparam name="TEvent">Event đang được gửi.</typeparam>
/// <param name="applicationName">
/// Deployable nào đang publish, viết dạng kebab-case — nửa sau của source URN.
/// </param>
/// <remarks>
/// <para>
/// Có hai envelope gặp nhau trên message này và cả hai đều đúng. MassTransit bọc payload trong envelope
/// riêng của nó để có thể route, retry và fault; envelope đó thuộc về thư viện và có thể bị thay thế. Các
/// thuộc tính CloudEvents là envelope <b>nghiệp vụ</b> — chuyện gì đã xảy ra, ở đâu, khi nào, ai khẳng
/// định điều đó — và envelope này thì không được thay thế. Giữ chúng dưới dạng header cho phép cả hai
/// cùng tồn tại mà không cái nào giả vờ là cái kia. Xem <c>ADR-008</c>.
/// </para>
/// <para>
/// Dùng header thay vì body vì body thuộc về MassTransit. Một reader ở ngoài .NET — một operator dùng
/// <c>rabbitmqadmin</c>, một cầu nối sang hệ thống khác, một message nằm trong error queue mà không code
/// nào deserialize được — vẫn có thể thấy message này tự nhận là gì.
/// </para>
/// </remarks>
internal sealed class CloudEventsSendFilter<TEvent>(string applicationName)
    : IFilter<SendContext<TEvent>>, IFilter<PublishContext<TEvent>>
    where TEvent : class, IDomainEvent
{
    private static readonly EventTypeName TypeName = EventTypeName.Of(typeof(TEvent));

    private readonly string _applicationName = applicationName;

    /// <inheritdoc />
    public Task Send(SendContext<TEvent> context, IPipe<SendContext<TEvent>> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Stamp(context);

        return next.Send(context);
    }

    /// <inheritdoc />
    public Task Send(PublishContext<TEvent> context, IPipe<PublishContext<TEvent>> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Stamp(context);

        return next.Send(context);
    }

    /// <inheritdoc />
    public void Probe(ProbeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.CreateFilterScope("nvm-cloudevents");
    }

    // Publish và send là hai pipe tách biệt trong MassTransit, và một filter gắn trên pipe này sẽ không
    // chạy trên pipe kia. Mọi thứ hệ thống này phát ra đều đi qua Publish, nên nếu chỉ cài trên send pipe
    // thì sẽ không đóng dấu được gì cả — và các header đơn giản là sẽ vắng mặt, không có lỗi nào báo cho
    // biết.
    private void Stamp(SendContext<TEvent> context)
    {
        var message = context.Message;

        if (context.TryGetPayload<StoredCloudEventHeaders>(out var stored))
        {
            stored.Apply(context);
            return;
        }

        context.Headers.Set(CloudEventHeaders.SpecVersion, CloudEventEnvelope<TEvent>.SpecVersionValue);

        // Lấy thẳng từ payload, không tạo ra ở đây. Giá trị này là thứ mà cả deduplication ở ingestion
        // lẫn deduplication trong command pipeline đều dùng làm khóa; có thêm một nguồn thứ hai cho nó
        // nghĩa là có thêm một thứ có thể lệch nhau.
        context.Headers.Set(CloudEventHeaders.Id, message.EventId.ToString());

        context.Headers.Set(CloudEventHeaders.Type, TypeName.Value);
        context.Headers.Set(CloudEventHeaders.Source, EventSource.Create(message.SiteId, _applicationName).Value);

        // Định dạng round-trip: rõ ràng và không mất thông tin. RFC 3339 cũng cho phép phần thập phân bị
        // cắt bớt, đó là cách System.Text.Json ghi trong JSON của event store — cùng một thời điểm, viết
        // theo hai cách. Đã ghi chú trong ADR-008 để không ai đọc nhầm đó là một sự sai lệch.
        context.Headers.Set(CloudEventHeaders.Time, message.OccurredAt.ToString("O", CultureInfo.InvariantCulture));

        context.Headers.Set(CloudEventHeaders.DataContentType, CloudEventEnvelope<TEvent>.JsonContentType);
    }

}
