using System.Reflection;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nvm.Bus.CloudEvents;
using Nvm.Bus.Topology;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Hosting;
using RabbitMQ.Client;

namespace Nvm.Bus;

/// <summary>Kết nối (wire) Manufacturing Service Bus vào một host.</summary>
public static class BusServiceCollectionExtensions
{
    /// <summary>Tên mà bus của tiến trình này báo cáo dưới <c>/health/ready</c>.</summary>
    /// <remarks>
    /// Là <c>bus</c>, không phải <c>masstransit-bus</c>. Mọi probe khác trong hệ thống này đều được đặt
    /// tên theo thứ nó kiểm tra — <c>sqlserver</c>, <c>postgres</c>, <c>rabbitmq</c> — và probe này kiểm
    /// tra bus, không phải thư viện triển khai nó. Ai đọc thấy dòng đỏ lúc 3 giờ sáng cần biết dependency
    /// nào đang gặp vấn đề, rồi sẽ đi tìm một probe khác, tên khác, cho RabbitMQ
    /// — và đó chính xác là thứ nên tìm, vì nó tồn tại và mang ý nghĩa khác.
    /// </remarks>
    public const string HealthCheckName = "bus";

    /// <summary>Đăng ký MassTransit với RabbitMQ theo topology của hệ thống này.</summary>
    /// <param name="services">Container đang được xây dựng.</param>
    /// <param name="configureOptions">Cấu hình kết nối.</param>
    /// <param name="registerConsumers">
    /// Nơi một host thêm các consumer của nó. Để trống nếu host chỉ publish.
    /// </param>
    /// <exception cref="InvalidOperationException">Các option không mô tả được một broker có thể kết nối tới.</exception>
    public static IServiceCollection AddNvmBus(
        this IServiceCollection services,
        Action<NvmBusOptions> configureOptions,
        Action<IBusRegistrationConfigurator>? registerConsumers = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new NvmBusOptions();
        configureOptions(options);
        options.Validate();

        services.AddMassTransit(bus =>
        {
            // Tên queue xuất phát từ việc consumer dùng để làm gì, không phải từ tên class của nó.
            bus.SetEndpointNameFormatter(NvmEndpointNameFormatter.Instance);

            ConfigureHealthCheck(bus);

            registerConsumers?.Invoke(bus);

            // Áp dụng cho mọi receive endpoint, kể cả những cái một Functional Block thêm vào sau này.
            // Nếu đặt riêng lẻ trên từng endpoint, đây là kiểu thứ hay bị copy bốn lần rồi
            // quên mất ở lần thứ năm.
            bus.AddConfigureEndpointsCallback((_, _, endpoint) =>
            {
                endpoint.UseMessageRetry(retry => retry.Intervals(NvmRetryPolicy.Intervals(Random.Shared)));

                // RabbitMQ 4 đã bỏ classic queue mirroring; quorum queue là thứ thay thế nó. Trên một
                // node development duy nhất, cả hai loại hoạt động giống hệt nhau, nên chọn sai ở đây sẽ
                // không lộ ra cho tới khi có node thứ hai — và lúc đó thì không thể sửa tại chỗ được nữa.
                // Đổi loại queue nghĩa là phải xóa nó, cùng với bất cứ thứ gì còn nằm bên trong.
                if (endpoint is IRabbitMqReceiveEndpointConfigurator rabbit)
                {
                    rabbit.SetQuorumQueue();
                }
            });

            bus.UsingRabbitMq((context, configurator) =>
            {
                configurator.Host(options.Host, options.Port, options.VirtualHost, host =>
                {
                    host.Username(options.Username);
                    host.Password(options.Password);
                });

                // Topology trước, và thứ tự này không phải để cho đẹp. MassTransit khóa entity name của
                // một message ngay khi có gì đó đọc nó, và việc cài đặt publish/send filter sẽ đọc
                // giá trị đó — nên nếu gọi UseNvmCloudEvents trước, SetEntityName bên dưới sẽ ném lỗi
                // "entity name was already evaluated" và tiến trình sẽ không bao giờ khởi động được.
                ApplyEventTopology(configurator);
                configurator.UseNvmCloudEvents(options.ApplicationName);

                configurator.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    /// <summary>Đăng ký một consumer cùng với queue và binding mà hệ thống này gán cho nó.</summary>
    /// <typeparam name="TConsumer">Consumer cần đặt lên bus.</typeparam>
    /// <param name="bus">Phần registration đang được xây dựng bên trong <see cref="AddNvmBus"/>.</param>
    /// <remarks>
    /// Đây là cách duy nhất được chấp nhận để thêm một consumer. <c>AddConsumer&lt;T&gt;</c> thuần cũng
    /// biên dịch được và cũng chạy được, nhưng sẽ tạo ra một queue bind vào context exchange với routing
    /// key rỗng — mà trên một topic exchange thì điều đó nghĩa là consumer không nhận được gì cả, và
    /// không có lỗi nào báo cho biết. Xem <see cref="Topology.NvmConsumerDefinition{TConsumer}"/>.
    /// </remarks>
    public static IBusRegistrationConfigurator AddNvmConsumer<TConsumer>(this IBusRegistrationConfigurator bus)
        where TConsumer : class, IConsumer
    {
        ArgumentNullException.ThrowIfNull(bus);

        bus.AddConsumer<TConsumer, NvmConsumerDefinition<TConsumer>>();

        return bus;
    }

    /// <summary>Khai báo tường minh tên và tag của bus health check thay vì kế thừa mặc định.</summary>
    /// <remarks>
    /// <para>
    /// <c>AddMassTransit</c> tự đăng ký một health check, và nếu để mặc định thì nó sẽ xuất hiện dưới
    /// tên <c>masstransit-bus</c> với tag tùy phiên bản thư viện lúc đó chọn. Cả hai đều là dữ kiện vận
    /// hành — tên xuất hiện trên dashboard và runbook, còn tag quyết định probe trả lời trên endpoint
    /// nào — nên cả hai đều được khai báo tường minh ở đây, cùng lý do mà tên queue được khai báo tường
    /// minh trong <see cref="Topology.BusEndpointAttribute"/> thay vì được suy ra.
    /// </para>
    /// <para>
    /// Tag chỉ là <b>ready, không bao giờ là live</b>. Check này fail khi bus không phục vụ được, và một
    /// tiến trình có bus không phục vụ được vẫn là một tiến trình không được phép restart — đặt nó vào
    /// liveness sẽ biến một sự cố broker thành vòng lặp restart trên mọi instance cùng lúc, đúng là điều
    /// N15 cấm.
    /// </para>
    /// <para>
    /// <b>Nó cho biết gì và không cho biết gì.</b> Nó báo cáo về bus của <i>chính tiến trình này</i>: bus
    /// đã khởi động chưa, và các receive endpoint của nó đã sẵn sàng chưa. Đây không phải là probe của
    /// broker. Một host chỉ publish thì không có receive endpoint nào, nên sau khi bus khởi động thành
    /// công, check này vẫn healthy kể cả khi broker đã biến mất — đã đo được ở M1/C14. Khả năng kết nối
    /// tới broker được trả lời bởi probe <c>rabbitmq</c> riêng biệt trong host, và hai thứ này không thể
    /// dùng thay cho nhau.
    /// </para>
    /// </remarks>
    private static void ConfigureHealthCheck(IBusRegistrationConfigurator bus) =>
        bus.ConfigureHealthCheckOptions(health =>
        {
            health.Name = HealthCheckName;

            // Xóa sạch, không phải thêm vào. Giá trị mặc định là do thư viện tự chọn, nếu chỉ thêm vào
            // thì check này sẽ trả lời trên một endpoint mà không ai ở đây quyết định là nó nên trả lời.
            health.Tags.Clear();
            health.Tags.Add(HealthTags.Ready);

            // Là Unhealthy, không phải Degraded như MassTransit sẽ báo cáo trong lúc bus vẫn đang khởi
            // động. Degraded trả về HTTP 200, nên một instance có bus không truyền được message vẫn sẽ
            // ở lại trong vòng xoay phục vụ — một readiness probe không bao giờ chuyển đỏ thì coi như
            // không kiểm tra gì cả.
            // (MassTransit 8.5 đánh dấu FailureStatus là obsolete; property này giờ set cả hai.)
            health.MinimalFailureStatus = HealthStatus.Unhealthy;
        });

    /// <summary>
    /// Trỏ mọi event đã khai báo tới topic exchange của context của nó, với routing key mà hệ
    /// thống này dùng.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mặc định của MassTransit là mỗi loại message có một exchange riêng, đặt tên theo type .NET. Điều
    /// đó sẽ biến việc đổi tên namespace thành một thay đổi topology, và không cho consumer cách nào để
    /// nói "bất cứ thứ gì xảy ra ở Hải Phòng" — một fan-out exchange theo từng type thì không có routing
    /// key nào để khớp cả.
    /// </para>
    /// <para>
    /// Vì vậy mọi event mang <c>[EventContract]</c> đều được chuyển hướng: exchange
    /// <c>nvm.{context}</c>, type <c>topic</c>, routing key
    /// <c>nvm.{site}.{context}.{event}.v{n}</c>. Được phát hiện bằng cách quét assembly contracts thay
    /// vì liệt kê ở đây, nên thêm một event chỉ cần một attribute chứ không phải sửa hai chỗ ở hai
    /// project.
    /// </para>
    /// </remarks>
    private static void ApplyEventTopology(IRabbitMqBusFactoryConfigurator configurator)
    {
        var apply = typeof(BusServiceCollectionExtensions)
            .GetMethod(nameof(ApplyTopologyFor), BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (var eventType in DeclaredEventTypes.All())
        {
            apply.MakeGenericMethod(eventType).Invoke(null, [configurator]);
        }
    }

    private static void ApplyTopologyFor<TEvent>(IRabbitMqBusFactoryConfigurator configurator)
        where TEvent : class, IDomainEvent
    {
        var eventType = EventTypeName.Of(typeof(TEvent));
        var exchange = NvmTopology.ExchangeFor(eventType);

        configurator.Message<TEvent>(message => message.SetEntityName(exchange));

        // Là Topic, không phải fanout mà MassTransit sẽ tự chọn. Fanout không có routing key, nên mọi
        // consumer bind vào exchange đó sẽ nhận mọi message trên đó rồi tự lọc bằng code — đúng là
        // cách một message ở Leipzig lại lọt vào một tiến trình ở Hải Phòng.
        configurator.Publish<TEvent>(publish => publish.ExchangeType = ExchangeType.Topic);

        configurator.Send<TEvent>(send => send.UseRoutingKeyFormatter(
            sendContext => RoutingKey.Create(sendContext.Message.SiteId, eventType).Value));
    }
}
