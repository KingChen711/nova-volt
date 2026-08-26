using System.Reflection;
using Nvm.Contracts.Events;

namespace Nvm.UnitTests.Contracts;

public sealed class EventVersionAttributeTests
{
    // Shaped like a real event so that this file also proves a positional record can satisfy
    // IDomainEvent with nothing added. If the interface ever grows a member that records cannot
    // supply cleanly, this stops compiling before anyone writes an event against it.
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
