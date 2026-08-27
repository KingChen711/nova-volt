using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;

namespace Nvm.UnitTests.FactoryModel;

/// <summary>
/// Activation is a read, a decision and a write, and something can happen in between. These pin the
/// compare-and-swap that makes the three into one.
/// </summary>
public sealed class ActiveFactoryModelConcurrencyTests
{
    private static readonly string SeedPath =
        Path.Combine(AppContext.BaseDirectory, "seed", FactoryModelSeed.FileName);

    private static FactorySite Site() =>
        FactoryModelSeed.Load(SeedPath).FindSite("NV1")
            ?? throw new InvalidOperationException("The seed file no longer describes plant NV1.");

    [Fact]
    public void ActivatingOnAStaleRead_IsRefusedRatherThanOverwriting()
    {
        // The scenario: a scheduled rollout and an engineer both read "nothing in force yet", and both
        // decide their revision is the one to install. Without the guard the plant ends up on whichever
        // wrote last, and two events go out each claiming to have moved it forward from the same place.
        var site = Site();
        var active = new InMemoryActiveFactoryModel();

        active.TryActivate(new ActiveFactoryModelRevision(1, site), expectedCurrentRevision: null)
            .ShouldBeTrue();

        active.TryActivate(new ActiveFactoryModelRevision(2, site), expectedCurrentRevision: null)
            .ShouldBeFalse();

        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(1);
    }

    [Fact]
    public void ActivatingOnAFreshRead_Succeeds()
    {
        // The other half. A guard that refuses everything would also pass the test above, so the
        // legitimate move has to be asserted next to it.
        var site = Site();
        var active = new InMemoryActiveFactoryModel();

        active.TryActivate(new ActiveFactoryModelRevision(1, site), expectedCurrentRevision: null).ShouldBeTrue();
        active.TryActivate(new ActiveFactoryModelRevision(2, site), expectedCurrentRevision: 1).ShouldBeTrue();

        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(2);
    }

    [Fact]
    public async Task SixteenActivationsAtOnce_ExactlyOneWins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Every caller reads the same current revision and then tries to move the plant on from it.
        // One must win and fifteen must be told they lost, because "the plant is on revision 12" is a
        // single fact and a work cell either exists on the shop floor or it does not.
        const int callers = 16;
        var site = Site();
        var active = new InMemoryActiveFactoryModel();
        active.TryActivate(new ActiveFactoryModelRevision(1, site), expectedCurrentRevision: null).ShouldBeTrue();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var arrived = new CountdownEvent(callers);

        var attempts = Enumerable.Range(2, callers)
            .Select(revision => Task.Run(
                async () =>
                {
                    arrived.Signal();
                    await start.Task;

                    return active.TryActivate(
                        new ActiveFactoryModelRevision(revision, site),
                        expectedCurrentRevision: 1);
                },
                cancellationToken))
            .ToArray();

        arrived.Wait(TimeSpan.FromSeconds(30), cancellationToken).ShouldBeTrue();
        start.SetResult();

        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(activated => activated).ShouldBe(1);
        active.Current("NV1").ShouldNotBeNull().Revision.ShouldNotBe(1);
    }

    [Fact]
    public void TwoPlantsMovingAtOnce_DoNotBlockOrOverwriteEachOther()
    {
        // Staged rollout. Hai Phong and Leipzig are separate facts, and a guard written as one lock
        // over one slot would have made them one.
        var snapshot = FactoryModelSeed.Load(SeedPath);
        var haiPhong = snapshot.FindSite("NV1").ShouldNotBeNull();
        var leipzig = snapshot.FindSite("DE1").ShouldNotBeNull();
        var active = new InMemoryActiveFactoryModel();

        active.TryActivate(new ActiveFactoryModelRevision(11, haiPhong), null).ShouldBeTrue();
        active.TryActivate(new ActiveFactoryModelRevision(11, leipzig), null).ShouldBeTrue();
        active.TryActivate(new ActiveFactoryModelRevision(12, haiPhong), 11).ShouldBeTrue();

        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(12);
        active.Current("DE1").ShouldNotBeNull().Revision.ShouldBe(11);
    }
}
