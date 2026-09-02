using MassTransit;
using Nvm.Bus;
using Nvm.Bus.Topology;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.UnitTests.Bus;

public sealed class NvmTopologyTests
{
    private static readonly EventTypeName RevisionActivated =
        EventTypeName.Of(typeof(FactoryModelRevisionActivated));

    [Fact]
    public void ExchangeFor_IsNamedAfterTheBoundedContextNotTheMessageType()
    {
        // Một exchange cho mỗi context, không phải mỗi event. Một context có chủ sở hữu và có vòng
        // đời; một message type thì không, và thêm một event thứ tư vào Traceability không nên tạo
        // thêm một exchange thứ tư để mọi consumer phải tự khám phá.
        NvmTopology.ExchangeFor(RevisionActivated).ShouldBe("nvm.factory-model");
        NvmTopology.ExchangeFor("traceability").ShouldBe("nvm.traceability");
    }

    [Fact]
    public void EventTypeName_IsReadFromTheAttributesRatherThanTheClassName()
    {
        // Tên trên wire được khai báo trong [EventContract], không được suy ra từ
        // FactoryModelRevisionActivated. Nếu suy ra thì một lần rename bằng IDE sẽ biến thành một
        // thay đổi topology mà không gì cảnh báo cả.
        RevisionActivated.Value.ShouldBe("com.novavolt.factory-model.revision-activated.v1");
    }

    [Fact]
    public void BindingForSite_SubscribesToOnePlantAndNothingElse()
    {
        // Multiplant nằm ở routing key chứ không phải ở một filter trong từng consumer: một service
        // bind ở đây không bao giờ nhận được message từ Leipzig, nên nó không thể để lọt một message
        // vì quên kiểm tra (AGENTS.md K3).
        NvmTopology.BindingForSite("NV1").ShouldBe("nvm.NV1.#");
    }

    [Fact]
    public void BindingForContext_NarrowsToOneContextAtOnePlant()
    {
        NvmTopology.BindingForContext("DE1", "quality").ShouldBe("nvm.DE1.quality.#");
    }

    [Fact]
    public void BindingForEventAtEverySite_UsesTheSingleSegmentWildcard()
    {
        // '*' khớp đúng một segment, '#' khớp phần còn lại. Dùng '#' cho site cũng sẽ khớp mọi event
        // khác trong hệ thống, giao tất cả chúng cho một consumer chỉ yêu cầu một event.
        NvmTopology.BindingForEventAtEverySite(RevisionActivated)
            .ShouldBe("nvm.*.factory-model.revision-activated.v1");
    }

    [Fact]
    public void BindingForEvent_IsExactlyTheRoutingKeyThePublisherUses()
    {
        // Binding hẹp nhất chính là bản thân routing key. Assert rằng chúng là cùng một string chính
        // là phép kiểm tra rằng subscription của consumer và địa chỉ của publisher không thể lệch nhau.
        NvmTopology.BindingForEvent("NV1", RevisionActivated)
            .ShouldBe(RoutingKey.Create("NV1", RevisionActivated).Value);
    }

    [Theory]
    [InlineData("nv1")]
    [InlineData("Nv1")]
    [InlineData("NV-1")]
    public void Binding_WithASiteThatIsNotUpperCase_IsRefused(string siteId)
    {
        // Cái bẫy mà cả quy ước này tồn tại để chặn lại. AMQP so sánh routing key byte theo byte, nên
        // một publisher trên nvm.NV1.* và một consumer bind vào nvm.nv1.# không bao giờ gặp nhau — và
        // broker không báo cáo gì cả. Từ chối ngay tại đây là thời điểm duy nhất ai đó phát hiện ra.
        Should.Throw<FormatException>(() => NvmTopology.BindingForSite(siteId));
    }

    [Fact]
    public void EndpointNameFormatter_NamesAQueueAfterWhatTheConsumerIsFor()
    {
        NvmEndpointNameFormatter.Instance.Consumer<CacheUpdaterProbe>().ShouldBe("nvm.factory-model.cache-updater");
    }

    [Fact]
    public void EndpointNameFormatter_RefusesAConsumerThatHasNotDeclaredItsQueue()
    {
        // Nếu không, MassTransit sẽ đặt tên queue theo tên class. Lần rename tiếp theo là đúng đắn,
        // build vẫn xanh, và service âm thầm bắt đầu đọc từ một queue rỗng mới trong khi queue cũ vẫn
        // giữ các message không ai xử lý.
        var thrown = Should.Throw<InvalidOperationException>(
            () => NvmEndpointNameFormatter.Instance.Consumer<UndeclaredProbe>());

        thrown.Message.ShouldContain(nameof(UndeclaredProbe));
        thrown.Message.ShouldContain("BusEndpoint");
    }

    [Fact]
    public void BusOptions_WithoutCredentials_AreRefusedWhileTheContainerIsBuilt()
    {
        // Fail ngay lúc startup thành một dòng rõ ràng, thay vì hai giờ sau đó dưới dạng một consumer
        // chưa từng nhận được gì và một broker log không ai theo dõi.
        var options = new NvmBusOptions { Username = "nvm", Password = "" };

        var thrown = Should.Throw<InvalidOperationException>(options.Validate);

        thrown.Message.ShouldContain("NVM_RABBITMQ_USER");
    }

    [Fact]
    public void EveryDeclaredEvent_HasBothAttributesSoItsTopologyCanBeBuilt()
    {
        // Bus xây dựng topology bằng cách quét tìm [EventContract]. Một event chỉ mang một attribute
        // mà thiếu attribute kia sẽ bị bỏ qua hoặc ném exception lúc startup, nên toàn bộ tập hợp
        // được kiểm tra ở đây thay vì kiểm tra từng event một.
        var events = typeof(IDomainEvent).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && type.IsAssignableTo(typeof(IDomainEvent)))
            .ToArray();

        events.ShouldNotBeEmpty();

        foreach (var declared in events)
        {
            Should.NotThrow(() => EventTypeName.Of(declared), $"{declared.Name} must declare its wire name");
        }
    }

    [BusEndpoint("factory-model", "cache-updater")]
    private sealed class CacheUpdaterProbe : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context) => Task.CompletedTask;
    }

    private sealed class UndeclaredProbe : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context) => Task.CompletedTask;
    }
}
