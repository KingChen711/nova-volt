using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.UnitTests.Bus;

public sealed class NvmSubscriptionTests
{
    [Fact]
    public void Of_BindsToTheContextExchangeAndTheEventsOwnRoutingKey()
    {
        // Publisher gửi tới nvm.factory-model với routing key
        // nvm.NV1.factory-model.revision-activated.v1. Cả hai nửa đều đến từ cùng một nơi, nên chúng
        // không thể lệch nhau thành một queue tồn tại, được bind, mà cứ mãi trống rỗng.
        var subscriptions = NvmSubscription.Of(typeof(CacheConsumer));

        subscriptions.ShouldHaveSingleItem();
        subscriptions[0].Exchange.ShouldBe("nvm.factory-model");
        subscriptions[0].RoutingKey.ShouldBe("nvm.*.factory-model.revision-activated.v1");
    }

    [Fact]
    public void Of_UsesTheSingleSegmentWildcardSoOneSubscriptionMeansOneEvent()
    {
        // '#' cũng sẽ khớp, và cũng sẽ giao mọi event khác trong hệ thống cho một consumer chỉ yêu
        // cầu một event — kể cả những event từ các bounded context mà nó không có quyền đọc.
        NvmSubscription.Of(typeof(CacheConsumer))[0].RoutingKey.ShouldNotContain("#");
    }

    [Fact]
    public void Of_GivesTwoConsumersOfOneEventTheSameSubscription()
    {
        // Fan-out không phải một tính chất của binding: cả hai consumer đều yêu cầu chính xác cùng
        // một tập message. Điều tách biệt chúng là mỗi consumer có queue riêng của mình — xem
        // NvmEndpointNameFormatter.
        NvmSubscription.Of(typeof(AuditConsumer))
            .ShouldBe(NvmSubscription.Of(typeof(CacheConsumer)));
    }

    [Fact]
    public void Of_DerivesOneSubscriptionPerEventTheConsumerHandles()
    {
        // Được đọc trực tiếp từ các interface IConsumer<T> thay vì khai báo thêm một lần nữa. Một
        // consumer nhận thêm một event khác sẽ có binding của nó đến từ chính lần sửa đã thêm
        // interface đó.
        NvmSubscription.Of(typeof(TwoEventConsumer)).Count.ShouldBe(1);
    }

    [Fact]
    public void Of_RefusesAConsumerThatHandlesNothingDeclaredAsAnEventContract()
    {
        // Bị từ chối ngay lúc startup, một cách rõ ràng. Nếu âm thầm không bind gì cả thì sẽ tạo ra
        // một service vẫn chạy, vẫn xanh trên mọi dashboard, mà không xử lý một message nào.
        var refusal = Should.Throw<InvalidOperationException>(
            () => NvmSubscription.Of(typeof(UndeclaredMessageConsumer)));

        refusal.Message.ShouldContain(nameof(UndeclaredMessageConsumer));
        refusal.Message.ShouldContain("EventContract");
    }

    private sealed class CacheConsumer : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context) => Task.CompletedTask;
    }

    private sealed class AuditConsumer : IConsumer<FactoryModelRevisionActivated>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context) => Task.CompletedTask;
    }

    // Xử lý một event đã khai báo và một message thường. Chỉ event đã khai báo mới thuộc về bus này.
    private sealed class TwoEventConsumer
        : IConsumer<FactoryModelRevisionActivated>, IConsumer<NotAnEvent>
    {
        public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context) => Task.CompletedTask;

        public Task Consume(ConsumeContext<NotAnEvent> context) => Task.CompletedTask;
    }

    private sealed class UndeclaredMessageConsumer : IConsumer<NotAnEvent>
    {
        public Task Consume(ConsumeContext<NotAnEvent> context) => Task.CompletedTask;
    }

    private sealed record NotAnEvent(string Anything);
}
