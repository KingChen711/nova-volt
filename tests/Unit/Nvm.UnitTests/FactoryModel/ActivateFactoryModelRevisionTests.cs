using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.FactoryModel;
using Nvm.FactoryModel;
using Nvm.FactoryModel.Commands;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Audit;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.UnitTests.FactoryModel;

public sealed class ActivateFactoryModelRevisionTests
{
    private static readonly DateTimeOffset ShiftAStart = new(2026, 8, 25, 6, 0, 0, TimeSpan.FromHours(7));

    private static readonly string SeedPath =
        Path.Combine(AppContext.BaseDirectory, "seed", FactoryModelSeed.FileName);

    private static ServiceProvider BuildContainer(FakeTimeProvider clock) =>
        new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddNvmFactoryModel(SeedPath)
            .AddNvmKernel(typeof(ActivateFactoryModelRevisionCommand).Assembly)
            .BuildServiceProvider();

    private static ActivateFactoryModelRevisionCommand Activate(string siteId, int revision) =>
        new(ActivateFactoryModelRevisionCommand.KeyFor(siteId, revision), siteId, revision);

    [Fact]
    public async Task Activating_APlantForTheFirstTime_ReportsEveryNodeAsAdded()
    {
        // Nothing was in force, so the whole tree is new. The event has to say so rather than report an
        // empty change: a consumer starting from nothing needs the full picture from the first message.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var activated = await dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken);

        activated.SiteId.ShouldBe("NV1");
        activated.Revision.ShouldBe(1);
        activated.NodeCount.ShouldBe(activated.EquipmentPathsAdded.Count);
        activated.EquipmentPathsRemoved.ShouldBeEmpty();
        activated.EquipmentPathsAdded.ShouldContain("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");
    }

    [Fact]
    public async Task Activating_OnePlant_LeavesTheOtherAlone()
    {
        // Multiplant, asserted rather than assumed. A staged rollout means Hai Phong can move while
        // Leipzig stays put, and a change at one plant must never appear in the other's event.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var active = container.GetRequiredService<IActiveFactoryModel>();

        var nv1 = await dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken);

        active.Current("DE1").ShouldBeNull();
        nv1.EquipmentPathsAdded.ShouldNotContain(path => path.StartsWith("NOVAVOLT/DE1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Activating_TheSameRevisionAgain_IsRefusedBecauseItMovesNothingForward()
    {
        // Not the same thing as a duplicate. A duplicate carries the same idempotency key and is
        // replayed silently; this is a different intention that happens to be pointless, and emitting a
        // second event would make every consumer rebuild its cache for a change that did not happen.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        await dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken);

        var repeat = new ActivateFactoryModelRevisionCommand(
            IdempotencyKey.FromNaturalKey("factory-model", "second-attempt", "NV1"),
            "NV1",
            1);

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(repeat, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("would not move it forward");
    }

    [Fact]
    public async Task Activating_ARevisionTheDocumentDoesNotHold_IsRefused()
    {
        // The revision is a guard, not a selector: the caller says which revision it read. Activating a
        // document nobody looked at is how a decommissioned work cell reappears on the shop floor.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(Activate("NV1", 99), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("revision 1");
    }

    [Fact]
    public async Task Activating_APlantThatIsNotInTheDocument_IsRefused()
    {
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(Activate("XX9", 1), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("XX9");
    }

    [Theory]
    [InlineData("nv1", 1, "lower-case site")]
    [InlineData("NV1", 0, "revision below 1")]
    [InlineData("", 1, "no site at all")]
    public async Task MalformedCommand_IsStoppedByValidationBeforeTheHandlerSeesIt(
        string siteId,
        int revision,
        string reason)
    {
        // A shape problem, so it fails as a validation error rather than a business refusal. The caller
        // can tell "fix your request" from "the world is not in the state you assumed".
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        await Should.ThrowAsync<CommandValidationException>(
            () => dispatcher.DispatchAsync(
                new ActivateFactoryModelRevisionCommand(
                    IdempotencyKey.FromNaturalKey("factory-model", "malformed", siteId, reason),
                    siteId,
                    revision),
                TestContext.Current.CancellationToken),
            reason);
    }

    [Fact]
    public async Task SameActivationTwice_RunsOnceAndReplaysTheEvent()
    {
        // The full loop the milestone is really about: command, three behaviours, handler, event — and
        // a resend that changes nothing. AGENTS.md K7 exercised through a real handler rather than a
        // probe.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var sink = (InMemoryCommandAuditSink)container.GetRequiredService<ICommandAuditSink>();

        var first = await dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken);
        var second = await dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken);

        second.ShouldBe(first);
        sink.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EventId_EqualsTheCommandsIdempotencyKey()
    {
        // The join between the two deduplication layers. Ingestion drops repeated device messages by
        // this value and the pipeline drops repeated commands by it; if the event carried a different
        // id, each layer would be keyed on something the other has never seen.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var command = Activate("NV1", 1);

        var activated = await dispatcher.DispatchAsync(command, TestContext.Current.CancellationToken);

        activated.EventId.ShouldBe(command.IdempotencyKey.Value);
    }

    [Fact]
    public async Task OccurredAt_ComesFromTheInjectedClock()
    {
        var clock = new FakeTimeProvider(ShiftAStart);
        await using var container = BuildContainer(clock);
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        clock.Advance(TimeSpan.FromHours(9));

        var activated = await dispatcher.DispatchAsync(Activate("DE1", 1), TestContext.Current.CancellationToken);

        activated.OccurredAt.ShouldBe(ShiftAStart.AddHours(9));
    }

    [Fact]
    public void Event_IsVersionedFromV1AndCarriesNoBareDateTime()
    {
        // AGENTS.md K6 and K2, checked on the first real event rather than left for the analyser that
        // arrives later in the milestone. An event without a version cannot be upcast in 2036, and a
        // DateTime without an offset is ambiguous for one hour every autumn at DE1.
        var eventType = typeof(FactoryModelRevisionActivated);

        eventType.GetCustomAttribute<EventVersionAttribute>()!.Version.ShouldBe(1);
        eventType.GetProperties().ShouldNotContain(property => property.PropertyType == typeof(DateTime));
    }

    [Fact]
    public void KeyFor_IsDeterministicAndSeparatesSitesAndRevisions()
    {
        ActivateFactoryModelRevisionCommand.KeyFor("NV1", 1)
            .ShouldBe(ActivateFactoryModelRevisionCommand.KeyFor("NV1", 1));

        ActivateFactoryModelRevisionCommand.KeyFor("NV1", 1)
            .ShouldNotBe(ActivateFactoryModelRevisionCommand.KeyFor("DE1", 1));

        ActivateFactoryModelRevisionCommand.KeyFor("NV1", 1)
            .ShouldNotBe(ActivateFactoryModelRevisionCommand.KeyFor("NV1", 2));
    }
}
