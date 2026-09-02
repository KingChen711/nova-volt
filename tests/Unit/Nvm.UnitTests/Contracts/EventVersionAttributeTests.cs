using System.Reflection;
using Nvm.Contracts.Events;

namespace Nvm.UnitTests.Contracts;

public sealed class EventVersionAttributeTests
{
    // Được tạo hình giống một event thật để file này cũng chứng minh rằng một positional record có
    // thể thỏa mãn IDomainEvent mà không cần thêm gì cả. Nếu interface này từng có thêm một member mà
    // record không thể cung cấp gọn gàng, việc này sẽ ngừng compile trước khi ai đó kịp viết một event
    // dựa trên nó.
    [EventVersion(1)]
    private sealed record ProbeEvent(Guid EventId, DateTimeOffset OccurredAt, string SiteId) : IDomainEvent;

    [EventVersion(2)]
    private record VersionedBase;

    private sealed record DerivedFromVersionedBase : VersionedBase;

    [Fact]
    public void GetCustomAttribute_OnAnnotatedEvent_ExposesTheDeclaredVersion()
    {
        var attribute = typeof(ProbeEvent).GetCustomAttribute<EventVersionAttribute>();

        attribute.ShouldNotBeNull();
        attribute.Version.ShouldBe(1);
    }

    [Fact]
    public void GetCustomAttribute_OnDerivedTypeAskingForInherited_FindsNothing()
    {
        var attribute = typeof(DerivedFromVersionedBase).GetCustomAttribute<EventVersionAttribute>(inherit: true);

        attribute.ShouldBeNull(
            "a derived event is a different wire type; inheriting the base version would give two payloads the same v-number");
    }
}
