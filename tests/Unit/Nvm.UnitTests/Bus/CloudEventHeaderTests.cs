using System.Globalization;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nvm.Bus.CloudEvents;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.UnitTests.Bus;

public sealed class CloudEventHeaderTests
{
    private static readonly Guid KnownEventId = Guid.Parse("0198f3a1-7c2e-5b4d-9e11-3a7f2c9b0d44");

    private static readonly DateTimeOffset KnownTime =
        new(2026, 8, 25, 3, 15, 42, 128, TimeSpan.Zero);

    private static FactoryModelRevisionActivated AnEvent(string siteId = "NV1") =>
        new(KnownEventId, KnownTime, siteId, 1, 41, [], []);

    private static async Task<ITestHarness> StartHarnessAsync(ServiceProvider provider)
    {
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        return harness;
    }

    private static ServiceProvider BuildHarness() =>
        new ServiceCollection()
            .AddMassTransitTestHarness(bus =>
            {
                bus.AddConsumer<RecordingProbeConsumer>();

                // Filter thật, chạy trên in-memory transport. Nếu thay bằng việc tự stamp header
                // ngay trong test thì test vẫn pass kể cả khi filter bị xóa mất.
                bus.UsingInMemory((context, configurator) =>
                {
                    configurator.UseNvmCloudEvents("host-all");
                    configurator.ConfigureEndpoints(context);
                });
            })
            .AddSingleton<ReceivedAttributes>()
            .BuildServiceProvider(true);

    [Fact]
    public async Task EveryOutgoingEvent_CarriesTheMandatoryCloudEventsAttributes()
    {
        // Đây là trọng tâm của cả commit: envelope ở §7.4 không chỉ nằm trên tài liệu. Một operator
        // dùng rabbitmqadmin, một bridge sang hệ thống khác, hay một message đang nằm trong error
        // queue mà không code nào deserialize nổi — tất cả vẫn có thể thấy message tự nhận là gì.
        await using var provider = BuildHarness();
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(AnEvent(), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        var attributes = Read(received);
        attributes.SpecVersion.ShouldBe("1.0");
        attributes.Type.Value.ShouldBe("com.novavolt.factory-model.revision-activated.v1");
        attributes.Source.Value.ShouldBe("urn:novavolt:nv1:host-all");
        attributes.Time.ShouldBe(KnownTime);
        attributes.DataContentType.ShouldBe("application/json");
    }

    [Fact]
    public async Task AnEventSentStraightToAnEndpoint_IsStampedToo()
    {
        // Publish và Send là hai pipe tách biệt, và một filter gắn trên pipe này không chạy trên pipe
        // kia. Mọi event hệ thống này phát ra đều đi qua Publish, nên send pipe là cái không ai từng
        // đụng tới — và chính vì thế nó là cái sẽ mục ruỗng mà không ai hay. Nếu thiếu test này, xóa
        // registration ConfigureSend vẫn để cả suite xanh trong khi một send trực tiếp tới endpoint
        // lại không mang bất kỳ CloudEvents metadata nào.
        await using var provider = BuildHarness();
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        // Được suy ra, không hard-code: đổi tên consumer thì địa chỉ này phải đổi theo, nếu không
        // test sẽ gửi vào khoảng không và fail vì một lý do chẳng liên quan gì tới filter cả.
        var endpoint = await harness.Bus.GetSendEndpoint(new Uri(
            harness.Bus.Address,
            DefaultEndpointNameFormatter.Instance.Consumer<RecordingProbeConsumer>()));
        await endpoint.Send(AnEvent(), TestContext.Current.CancellationToken);

        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        var attributes = Read(received);
        attributes.SpecVersion.ShouldBe("1.0");
        attributes.Type.Value.ShouldBe("com.novavolt.factory-model.revision-activated.v1");
        attributes.Source.Value.ShouldBe("urn:novavolt:nv1:host-all");
        attributes.DataContentType.ShouldBe("application/json");
    }

    [Fact]
    public async Task CloudEventId_IsTheEventIdAndThereforeTheCommandsIdempotencyKey()
    {
        // Điểm nối giữa hai lớp deduplication, được kiểm tra trên wire thay vì trong một record.
        // Ingestion loại bỏ device message trùng lặp dựa trên giá trị này, và command pipeline cũng
        // loại bỏ command trùng lặp dựa trên nó; có thêm một nguồn thứ hai cho giá trị này thì sẽ có
        // thêm một thứ có thể lệch nhau.
        await using var provider = BuildHarness();
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(AnEvent(), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        Read(received).Id.ShouldBe(KnownEventId);
    }

    [Fact]
    public async Task Source_NamesThePlantTheEventCameFromNotTheProcessesDefault()
    {
        // Source là site cộng với application, và site được lấy từ payload. Một process publish cho
        // cả hai nhà máy không được phép stamp mọi message bằng nhà máy mà nó khởi động cùng.
        await using var provider = BuildHarness();
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(AnEvent("DE1"), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        var source = Read(received).Source;
        source.SiteId.ShouldBe("DE1");
        source.Value.ShouldBe("urn:novavolt:de1:host-all");
    }

    [Fact]
    public void HeaderNames_UseTheKafkaStylePrefixThisSystemChose()
    {
        // AMQP 0-9-1 không có binding cho CloudEvents, nên prefix này là một quy ước cục bộ chứ không
        // phải một chuẩn. Được pin ở đây để không thể lệch đi, và được giải thích trong ADR-008.
        CloudEventHeaders.SpecVersion.ShouldBe("ce_specversion");
        CloudEventHeaders.Id.ShouldBe("ce_id");
        CloudEventHeaders.Type.ShouldBe("ce_type");
        CloudEventHeaders.Source.ShouldBe("ce_source");
        CloudEventHeaders.Time.ShouldBe("ce_time");
        CloudEventHeaders.DataContentType.ShouldBe("ce_datacontenttype");
    }

    [Fact]
    public async Task AMessageWithNoCloudEventsHeaders_ReadsAsNullRatherThanThrowing()
    {
        // Không phải mọi message trên bus đều đến từ publish path của hệ thống này. Các attribute
        // vắng mặt là một sự thật về message, không phải một lỗi cần báo cáo — nên harness này cố
        // tình không có filter.
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(bus => bus.AddConsumer<RecordingProbeConsumer>())
            .AddSingleton<ReceivedAttributes>()
            .BuildServiceProvider(true);
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(AnEvent(), TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        received.Value.ShouldBeNull();
        received.Error.ShouldBeNull();
    }

    [Theory]
    [InlineData(CloudEventHeaders.SpecVersion)]
    [InlineData(CloudEventHeaders.Id)]
    [InlineData(CloudEventHeaders.Type)]
    [InlineData(CloudEventHeaders.Source)]
    [InlineData(CloudEventHeaders.Time)]
    [InlineData(CloudEventHeaders.DataContentType)]
    public async Task AMessageMissingAnyOneMandatoryAttribute_IsRefusedRatherThanReadAsAPartialSet(
        string omitted)
    {
        // Lần lượt từng header một, vì "tập hợp" này không có thành viên nào được ưu tiên. Kiểm tra
        // một header trước rồi coi việc nó vắng mặt là "message này không có attribute nào" chính là
        // con bug mà test này bao phủ: nó sẽ để một message thiếu đúng một header đó lọt qua như thể
        // không mang gì cả, trong khi năm header còn lại vẫn nằm đó nói điều ngược lại.
        //
        // Các header ở đây được viết bằng tay — mục đích là tạo một message mà filter thật sẽ không
        // bao giờ tạo ra, từ một publisher không đồng thuận với hệ thống này về tập hợp đó là gì.
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(bus => bus.AddConsumer<RecordingProbeConsumer>())
            .AddSingleton<ReceivedAttributes>()
            .BuildServiceProvider(true);
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(
            AnEvent(),
            context => StampEveryHeaderExcept(context, omitted),
            TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        received.Value.ShouldBeNull();

        var error = received.Error.ShouldNotBeNull();
        error.ShouldBeOfType<InvalidOperationException>();
        error.Message.ShouldContain(omitted);
        error.Message.ShouldContain("5 of the 6");
    }

    [Fact]
    public async Task AHeaderCarryingSomethingOtherThanText_IsMalformedRatherThanAbsent()
    {
        // Phiên bản ở mức type của con bug sentinel. Một header đọc bằng `as string` trả về null khi
        // nó mang bất kỳ kiểu gì khác, nên một message với sáu header — một trong số đó là số nguyên
        // — sẽ bị đọc như một message chỉ có năm header, và bị từ chối vì sai lý do, hoặc tệ hơn, bị
        // đọc như một message không có header nào.
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(bus => bus.AddConsumer<RecordingProbeConsumer>())
            .AddSingleton<ReceivedAttributes>()
            .BuildServiceProvider(true);
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(
            AnEvent(),
            context =>
            {
                StampEveryHeader(context);
                context.Headers.Set(CloudEventHeaders.Time, 1756090542);
            },
            TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        received.Value.ShouldBeNull();
        received.Error.ShouldNotBeNull().Message.ShouldContain(CloudEventHeaders.Time);
    }

    [Fact]
    public async Task AHeaderPresentButEmpty_IsMalformedRatherThanAbsent()
    {
        // CloudEvents coi "không có attribute" và "attribute với giá trị rỗng" là hai khẳng định khác
        // nhau — đó là lý do năm attribute tùy chọn được bỏ qua thay vì ghi trống. Đọc theo chiều
        // ngược lại, một ce_datacontenttype rỗng là publisher đang khẳng định một encoding là "", và
        // điều đó không được phép trôi qua như một tập hợp hợp lệ.
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(bus => bus.AddConsumer<RecordingProbeConsumer>())
            .AddSingleton<ReceivedAttributes>()
            .BuildServiceProvider(true);
        var harness = await StartHarnessAsync(provider);
        var received = provider.GetRequiredService<ReceivedAttributes>();

        await harness.Bus.Publish(
            AnEvent(),
            context =>
            {
                StampEveryHeader(context);
                context.Headers.Set(CloudEventHeaders.DataContentType, "   ");
            },
            TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<FactoryModelRevisionActivated>(TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        received.Value.ShouldBeNull();
        received.Error.ShouldNotBeNull().Message.ShouldContain(CloudEventHeaders.DataContentType);
    }

    private static CloudEventAttributes Read(ReceivedAttributes received)
    {
        var error = received.Error?.Message;

        // Được báo cáo trước khi kiểm tra null, để nếu một stamp bị xóa khỏi filter thì test fail với
        // tên của header bị thiếu, thay vì với "received.Value was null".
        error.ShouldBeNull();

        return received.Value.ShouldNotBeNull();
    }

    private static void StampEveryHeader(PublishContext<FactoryModelRevisionActivated> context) =>
        StampEveryHeaderExcept(context, omitted: string.Empty);

    private static void StampEveryHeaderExcept(
        PublishContext<FactoryModelRevisionActivated> context,
        string omitted)
    {
        var message = context.Message;

        var all = new (string Header, string Value)[]
        {
            (CloudEventHeaders.SpecVersion, "1.0"),
            (CloudEventHeaders.Id, message.EventId.ToString()),
            (CloudEventHeaders.Type, EventTypeName.Of(typeof(FactoryModelRevisionActivated)).Value),
            (CloudEventHeaders.Source, EventSource.Create(message.SiteId, "host-all").Value),
            (CloudEventHeaders.Time, message.OccurredAt.ToString("O", CultureInfo.InvariantCulture)),
            (CloudEventHeaders.DataContentType, "application/json"),
        };

        foreach (var (header, value) in all.Where(pair => pair.Header != omitted))
        {
            context.Headers.Set(header, value);
        }
    }

    public sealed class ReceivedAttributes
    {
        public CloudEventAttributes? Value { get; set; }

        public Exception? Error { get; set; }
    }

    public sealed class RecordingProbeConsumer(ReceivedAttributes received)
        : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context)
        {
            try
            {
                received.Value = context.CloudEvent();
            }
            catch (InvalidOperationException exception)
            {
                // Được ghi lại, không rethrow: throw ở đây sẽ trở thành một fault và năm lần retry,
                // trong khi assertion đang quan tâm tới việc reader từ chối cái gì, không phải bus
                // làm gì sau đó.
                received.Error = exception;
            }

            return Task.CompletedTask;
        }
    }
}
