using System.Reflection;
using Nvm.Contracts.Events;

namespace Nvm.ArchitectureTests;

/// <summary>A4, A5 — the shape every event must have, checked against built metadata.</summary>
/// <remarks>
/// <para>
/// A4 deliberately repeats what analyzer <c>NVM002</c> already refuses, and the repetition is the
/// point. An analyzer runs inside the compiler and can be silenced from inside the file it is
/// checking — one <c>#pragma warning disable NVM002</c> and it is gone. These tests read the
/// <b>metadata of the assembly that was produced</b>, where a pragma leaves no trace: the property is
/// either typed <c>DateTime</c> or it is not.
/// </para>
/// <para>
/// The control types below prove that rather than assert it. This project sets
/// <c>UseNvmAnalyzers=false</c>, so <c>EventWithForbiddenClock</c> is exactly the file an analyzer
/// never saw — and the same predicate that clears every real event flags it.
/// </para>
/// </remarks>
public sealed class EventContractTests
{
    private static IReadOnlyList<Type> ProductionEvents =>
        [.. NvmAssemblies.Contracts.GetTypes().Where(IsConcreteEvent)];

    [Fact]
    public void ThereAreEventsToCheckAtAll()
    {
        // The guard that makes every other test in this class mean something. A misspelled filter
        // returns an empty sequence, and every "all of them are fine" assertion below passes.
        ProductionEvents.ShouldNotBeEmpty();
    }

    [Fact]
    public void A4_NoEventCarriesADateTimeAnywhereInItsShape()
    {
        // Site DE1 observes daylight saving, so one hour every autumn happens twice. DateTime records
        // the wall-clock reading with no offset, the event store is append-only, and the ambiguity can
        // never be corrected afterwards (AGENTS.md K2).
        var offenders = ProductionEvents
            .SelectMany(MembersMentioningDateTime)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty($"DateTime reaches the wire through: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void A4_Control_TheDateTimeWalkFindsOneBuriedInAGeneric()
    {
        // The nesting case is where this kind of check is usually wrong: comparing only the outermost
        // type clears IReadOnlyList<DateTime>, which serializes exactly as badly as a bare field.
        MembersMentioningDateTime(typeof(EventWithForbiddenClock))
            .ShouldContain($"{nameof(EventWithForbiddenClock)}.{nameof(EventWithForbiddenClock.Samples)}");
    }

    [Fact]
    public void A5_EveryEventCarriesSiteId()
    {
        // Structurally true today, because IDomainEvent declares SiteId and the compiler enforces the
        // interface. Written down anyway: the day somebody publishes a payload that does not implement
        // the marker, this is the assertion that was already here to be moved.
        foreach (var type in ProductionEvents)
        {
            type.GetProperty(nameof(IDomainEvent.SiteId))
                .ShouldNotBeNull($"{type.Name} has no SiteId (AGENTS.md K3)");
        }
    }

    [Fact]
    public void A5_SiteIdIsNeverNullable()
    {
        // The half the interface does not enforce. A nullable SiteId compiles, satisfies IDomainEvent,
        // and produces a routing key with an empty first segment — which matches no binding, so the
        // event reaches nobody and nothing anywhere raises an error.
        var context = new NullabilityInfoContext();

        foreach (var type in ProductionEvents)
        {
            var siteId = type.GetProperty(nameof(IDomainEvent.SiteId))!;

            context.Create(siteId).ReadState.ShouldBe(
                NullabilityState.NotNull,
                $"{type.Name}.SiteId is nullable (AGENTS.md K3)");
        }
    }

    [Fact]
    public void A5_EveryEventDeclaresItsWireNameAndVersion()
    {
        // Both attributes, or the event is invisible to the bus. DeclaredEventTypes.All() selects on
        // IDomainEvent *and* [EventContract]; an event missing the attribute gets no exchange, no
        // routing key and no CloudEvents headers, and publishes into MassTransit's default topology
        // where nothing is bound. It runs, it throws nothing, and nobody receives it.
        foreach (var type in ProductionEvents)
        {
            type.GetCustomAttribute<EventContractAttribute>()
                .ShouldNotBeNull($"{type.Name} has no [EventContract], so the bus cannot route it");

            type.GetCustomAttribute<EventVersionAttribute>()
                .ShouldNotBeNull($"{type.Name} has no [EventVersion] (AGENTS.md K6)")
                .Version.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void A5_Control_TheAttributeCheckSeesAnEventThatIsMissingThem()
    {
        // EventWithForbiddenClock carries neither attribute and was compiled with the analyzers off.
        typeof(EventWithForbiddenClock).GetCustomAttribute<EventContractAttribute>().ShouldBeNull();
        typeof(EventWithForbiddenClock).GetCustomAttribute<EventVersionAttribute>().ShouldBeNull();
    }

    [Fact]
    public void EventsLiveOnlyInContracts()
    {
        // DeclaredEventTypes.All() scans exactly one assembly. An event declared inside a Functional
        // Block is never discovered, so it never gets topology — the same silent nothing as a missing
        // attribute, one level up.
        foreach (var assembly in new[] { NvmAssemblies.Kernel, NvmAssemblies.Bus, NvmAssemblies.FactoryModel })
        {
            assembly.GetTypes()
                .Where(IsConcreteEvent)
                .ShouldBeEmpty($"{assembly.GetName().Name} declares an event; events belong in Nvm.Contracts");
        }
    }

    private static bool IsConcreteEvent(Type type) =>
        type is { IsAbstract: false, IsInterface: false } && typeof(IDomainEvent).IsAssignableFrom(type);

    /// <summary>Names the members of a type whose declared type mentions <c>DateTime</c>.</summary>
    private static IEnumerable<string> MembersMentioningDateTime(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => Mentions(property.PropertyType))
            .Select(property => $"{type.Name}.{property.Name}");

    /// <summary>Whether <c>DateTime</c> appears anywhere inside a type, however deeply nested.</summary>
    /// <remarks>
    /// <c>DateTime?</c> is <c>Nullable&lt;DateTime&gt;</c> and <c>DateTime[]</c> is an array type, so
    /// both fall out of the same two recursions rather than needing cases of their own.
    /// </remarks>
    private static bool Mentions(Type type) =>
        type == typeof(DateTime)
        || (type.IsArray && Mentions(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(Mentions));

    /// <summary>A deliberately wrong event, so the rules above can be shown to be able to fail.</summary>
    /// <remarks>
    /// It implements the marker, hides a <c>DateTime</c> inside a list, and declares neither contract
    /// attribute — every fault A4 and A5 look for, in one type no analyzer ever inspected.
    /// </remarks>
    public sealed record EventWithForbiddenClock(
        Guid EventId,
        DateTimeOffset OccurredAt,
        string SiteId,
        IReadOnlyList<DateTime> Samples) : IDomainEvent;
}
