using MassTransit;
using RabbitMQ.Client;

namespace Nvm.Bus.Topology;

/// <summary>
/// Cấp cho một consumer queue của riêng nó, bind vào context exchange bằng routing key của mọi event
/// mà nó tiêu thụ.
/// </summary>
/// <typeparam name="TConsumer">Consumer được đặt lên bus.</typeparam>
/// <remarks>
/// <para>
/// Đây là nửa topology mà MassTransit không thể tự suy ra được. Nửa publish — một topic exchange cho
/// mỗi bounded context, routing key <c>nvm.{site}.{context}.{event}.v{n}</c> — được thiết lập trong
/// <see cref="BusServiceCollectionExtensions"/>. Trên một exchange kiểu <b>topic</b>, không gì được
/// gửi tới cho đến khi có ai đó nêu ra một pattern để khớp, và binding mặc định của MassTransit mang
/// một routing key rỗng, thứ không khớp với bất cứ gì cả. Vì vậy một consumer được nối dây mà không có
/// definition này sẽ khởi động sạch sẽ, xuất hiện trên management UI, và không bao giờ nhận được gì.
/// </para>
/// <para>
/// Nó subscribe vào cái gì được tính toán bởi <see cref="NvmSubscription"/> chứ không bao giờ được ráp
/// ở đây bằng cách nối chuỗi. AMQP so sánh routing key byte theo byte, nên <c>nvm.nv1.#</c> và
/// <c>nvm.NV1.#</c> là hai subscription khác nhau và broker không báo lỗi cho cả hai — biện pháp phòng
/// vệ duy nhất là để một đoạn code duy nhất xây dựng cả hai phía.
/// </para>
/// </remarks>
public sealed class NvmConsumerDefinition<TConsumer> : ConsumerDefinition<TConsumer>
    where TConsumer : class, IConsumer
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Consumer không xử lý gì thuộc một event contract đã khai báo, nên không có routing key nào để
    /// bind theo.
    /// </exception>
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(endpointConfigurator);

        // Được yêu cầu vô điều kiện, nên một consumer không có gì để subscribe sẽ bị từ chối lúc khởi
        // động ngay cả trên một transport không có exchange nào để bind.
        var subscriptions = NvmSubscription.Of(typeof(TConsumer));

        // Transport in-memory mà test harness dùng không có exchange nào, và yêu cầu nó bind một cái
        // sẽ fail. Bỏ qua ở đây giữ cho cùng một definition dùng được ở cả hai nơi, đó chính là mục
        // đích: một test tự cấu hình binding riêng của nó sẽ là đang test bản sao topology của chính nó.
        if (endpointConfigurator is not IRabbitMqReceiveEndpointConfigurator rabbit)
        {
            return;
        }

        // Nếu không, MassTransit sẽ tự khai báo context exchange, với exchange type mặc định của riêng
        // nó và một routing key rỗng. Hai vấn đề gộp làm một: khai báo đó bất đồng với `topic` của phía
        // publisher nên RabbitMQ trả lời PRECONDITION_FAILED, và binding sống sót thì không khớp với
        // message nào mà hệ thống này gửi cả.
        rabbit.ConfigureConsumeTopology = false;

        foreach (var subscription in subscriptions)
        {
            rabbit.Bind(subscription.Exchange, binding =>
            {
                // Phải khớp với cách publisher khai báo nó, byte theo byte — RabbitMQ từ chối khai báo
                // lại một exchange đã tồn tại với property khác, và lỗi này xuất hiện lúc khởi động như
                // một lỗi cấp channel chứ không phải như bất cứ điều gì liên quan tới topology.
                binding.ExchangeType = ExchangeType.Topic;
                binding.RoutingKey = subscription.RoutingKey;
            });
        }
    }
}
