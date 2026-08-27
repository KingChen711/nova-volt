using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.FactoryModel;
using Nvm.FactoryModel;
using Nvm.FactoryModel.Commands;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Audit;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.UnitTests.FactoryModel;

public sealed class ActivateFactoryModelRevisionTests
{
    private static readonly DateTimeOffset ShiftAStart = new(2026, 8, 25, 6, 0, 0, TimeSpan.FromHours(7));

    // What each published revision did to the plant, so the assertions below read as the change they
    // are checking rather than as strings. r2 widened Formation cycler 1 from four charging channels
    // to eight; r3 took cycler 2 out for a long overhaul, put a fourth stacker on cell line 2, and —
    // at Leipzig only — added an end-of-line tester.
    private const string Cycler2 = "NOVAVOLT/NV1/FORMATION/F1/FORM-02";
    private const string Stacker4 = "NOVAVOLT/NV1/ASSEMBLY/L2/STACK-04";
    private const string LeipzigEolTester = "NOVAVOLT/DE1/PACK/P1/EOL-01";

    private static readonly string SeedDirectory = Path.Combine(AppContext.BaseDirectory, "seed");

    private static ServiceProvider BuildContainer(FakeTimeProvider clock) =>
        new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddNvmFactoryModel(SeedDirectory)
            .AddNvmKernel(typeof(ActivateFactoryModelRevisionCommand).Assembly)
            .BuildServiceProvider();

    /// <summary>Walks a plant up through the given revisions and hands back the last event.</summary>
    private static async Task<FactoryModelRevisionActivated> RollForwardAsync(
        ICommandDispatcher dispatcher,
        string siteId,
        params int[] revisions)
    {
        FactoryModelRevisionActivated? last = null;

        foreach (var revision in revisions)
        {
            last = await dispatcher.DispatchAsync(
                Activate(siteId, revision),
                TestContext.Current.CancellationToken);
        }

        return last!;
    }

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
    public async Task Activating_ARevisionTheCatalogDoesNotHold_IsRefusedAndNamesWhatExists()
    {
        // The revision is a guard, not a selector: the caller says which revision it read. Refusing an
        // unpublished number is how a decommissioned work cell is kept off the shop floor — and the
        // refusal names the shelf, because "revision 99 does not exist" leaves the operator guessing
        // whether they mistyped or the rollout was never published.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(Activate("NV1", 99), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("revision 99");
        thrown.Message.ShouldContain("1, 2, 3");
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

    // ── Moving a plant from one revision to the next ────────────────────────────────────────────
    // Everything above activates revision 1 on a plant running nothing, where every path is new and
    // nothing is ever removed. That is the easy half, and until now it was the only half.

    [Fact]
    public async Task Activating_RevisionTwoWhileOnOne_ReportsTheNewChannelsAndRemovesNothing()
    {
        // Formation cycler 1 went from four charging channels to eight. Widening capacity takes
        // nothing away, so the removed list has to stay empty: a diff that reported churn on a pure
        // addition would send every consumer rebuilding a cache for equipment that never moved.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var activated = await RollForwardAsync(dispatcher, "NV1", 1, 2);

        activated.Revision.ShouldBe(2);
        activated.NodeCount.ShouldBe(35);
        activated.EquipmentPathsRemoved.ShouldBeEmpty();
        activated.EquipmentPathsAdded.ShouldBe([
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0005",
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0006",
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0007",
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0008"]);
    }

    [Fact]
    public async Task Activating_RevisionThreeWhileOnTwo_ReportsBothWhatArrivedAndWhatLeft()
    {
        // The case the plan asked for at C08 and the code could not perform. Cycler 2 went out for a
        // long overhaul and a fourth stacker went in, so exactly one path leaves and one arrives —
        // and the one that leaves is the half of the contract that had never run once.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var active = container.GetRequiredService<IActiveFactoryModel>();

        await RollForwardAsync(dispatcher, "NV1", 1, 2);
        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(2);

        var activated = await dispatcher.DispatchAsync(
            Activate("NV1", 3),
            TestContext.Current.CancellationToken);

        activated.Revision.ShouldBe(3);
        activated.NodeCount.ShouldBe(35);
        activated.EquipmentPathsAdded.ShouldBe([Stacker4]);
        activated.EquipmentPathsRemoved.ShouldBe([Cycler2]);
        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(3);
    }

    [Fact]
    public async Task Activating_ARevisionBehindTheOneInForce_IsRefused()
    {
        // Rolling back is not an activation. A plant that has told the world it moved to 3 cannot
        // quietly return to 2: the stream would then carry two contradictory claims and no consumer
        // could work out which tree is on the floor.
        //
        // A fresh idempotency key on purpose. Reusing the key from the earlier step would be replayed
        // as a duplicate and hand back the old event, which is correct behaviour and would test
        // nothing about the refusal.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        await RollForwardAsync(dispatcher, "NV1", 1, 2, 3);

        var rollback = new ActivateFactoryModelRevisionCommand(
            IdempotencyKey.FromNaturalKey("factory-model", "rollback-attempt", "NV1"),
            "NV1",
            2);

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(rollback, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("would not move it forward");
    }

    [Fact]
    public async Task TwoPlants_SitOnDifferentRevisions_WhichIsWhatAStagedRolloutIs()
    {
        // Hai Phong on 3 while Leipzig is still on 1. Not drift waiting to be corrected — a rollout
        // reaches one plant at a time, and a model that could not express this would force both plants
        // to move together, which is the one thing a factory never does.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var active = container.GetRequiredService<IActiveFactoryModel>();

        await RollForwardAsync(dispatcher, "NV1", 1, 2, 3);
        await dispatcher.DispatchAsync(Activate("DE1", 1), TestContext.Current.CancellationToken);

        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(3);
        active.Current("DE1").ShouldNotBeNull().Revision.ShouldBe(1);
    }

    [Fact]
    public async Task Activating_ASkippedRevision_DiffsAgainstWhatIsInForce_NotTheDocumentBeforeIt()
    {
        // Leipzig never took revision 2 — nothing in it concerned Leipzig. Going straight from 1 to 3
        // has to diff against what the plant is actually running, not against whichever document
        // happens to sit next to 3 on the shelf. Getting this wrong would report the Hai Phong
        // channels as arriving at Leipzig.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var activated = await RollForwardAsync(dispatcher, "DE1", 1, 3);

        activated.NodeCount.ShouldBe(10);
        activated.EquipmentPathsAdded.ShouldBe([LeipzigEolTester]);
        activated.EquipmentPathsRemoved.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheSameRollout_RunTwice_ProducesTheSameEventBothTimes()
    {
        // Deterministic, because these events end up in an audit trail and in a golden file. A payload
        // whose order came from hash iteration would differ between two runs of the same change, and
        // nothing would be comparable to anything ever again.
        var first = await RunRolloutAsync();
        var second = await RunRolloutAsync();

        second.EventId.ShouldBe(first.EventId);
        second.NodeCount.ShouldBe(first.NodeCount);
        second.EquipmentPathsAdded.ShouldBe(first.EquipmentPathsAdded);
        second.EquipmentPathsRemoved.ShouldBe(first.EquipmentPathsRemoved);

        async Task<FactoryModelRevisionActivated> RunRolloutAsync()
        {
            await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
            using var scope = container.CreateScope();

            return await RollForwardAsync(
                scope.ServiceProvider.GetRequiredService<ICommandDispatcher>(),
                "NV1",
                1,
                2,
                3);
        }
    }

    [Fact]
    public async Task ActiveRevision_IsLostOnRestart_WhileTheCatalogIsNot()
    {
        // The boundary M1 stops at, asserted rather than described in a comment nobody re-checks. The
        // documents are files, so a restart reads all three back; which revision each plant had in
        // force lived in RAM and is gone. A plant that comes back believing it runs nothing will
        // report its whole tree as added on the next activation. Closing that needs a database (M5).
        await using (var beforeRestart = BuildContainer(new FakeTimeProvider(ShiftAStart)))
        {
            using var scope = beforeRestart.CreateScope();
            await RollForwardAsync(
                scope.ServiceProvider.GetRequiredService<ICommandDispatcher>(),
                "NV1",
                1,
                2,
                3);

            beforeRestart.GetRequiredService<IActiveFactoryModel>()
                .Current("NV1").ShouldNotBeNull().Revision.ShouldBe(3);
        }

        await using var afterRestart = BuildContainer(new FakeTimeProvider(ShiftAStart));

        afterRestart.GetRequiredService<IActiveFactoryModel>().Current("NV1").ShouldBeNull();
        afterRestart.GetRequiredService<IFactoryModelCatalog>().Revisions.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Activating_WhenThePlantMovedUnderneath_IsRefusedRatherThanOverwriting()
    {
        // The compare-and-swap losing, seen from the handler rather than from the store. A scheduled
        // rollout and an engineer at the console read the same current revision; whichever writes
        // second has to be told it lost, because "the plant is on revision 3" is one fact and a work
        // cell is either on the floor or it is not.
        //
        // Forced with a stand-in rather than a real race, so this branch runs on every build instead
        // of on the builds where the scheduler happens to interleave the right way.
        await using var container = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(ShiftAStart))
            .AddSingleton<IActiveFactoryModel>(new AlwaysLosesTheRace())
            .AddNvmFactoryModel(SeedDirectory)
            .AddNvmKernel(typeof(ActivateFactoryModelRevisionCommand).Assembly)
            .BuildServiceProvider();
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("moved to another revision");
    }

    /// <summary>A plant that somebody else always moves first.</summary>
    private sealed class AlwaysLosesTheRace : IActiveFactoryModel
    {
        public ActiveFactoryModelRevision? Current(string siteId) => null;

        public bool TryActivate(ActiveFactoryModelRevision revision, int? expectedCurrentRevision) => false;
    }
}
