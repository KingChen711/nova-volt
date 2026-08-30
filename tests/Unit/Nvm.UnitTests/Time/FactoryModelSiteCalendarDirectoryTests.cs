using Microsoft.Extensions.Time.Testing;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;
using Nvm.FactoryModel.Time;
using Nvm.Time;

namespace Nvm.UnitTests.Time;

public sealed class FactoryModelSiteCalendarDirectoryTests
{
    // The real seed, not a fixture: the whole claim of this commit is that the zone comes from the
    // document a plant is actually running.
    private static readonly FactoryModelSnapshot Seed =
        FactoryModelSeed.Load(Path.Combine(AppContext.BaseDirectory, "seed", FactoryModelSeed.FileNameFor(3)));

    [Theory]
    [InlineData("NV1", "Asia/Ho_Chi_Minh")]
    [InlineData("DE1", "Europe/Berlin")]
    public void TheZoneComesFromTheFactoryModel(string siteId, string expectedIanaId)
    {
        var directory = DirectoryOver(Seed, siteId);

        var calendar = directory.Find(siteId);

        calendar.ShouldNotBeNull();
        calendar.SiteId.ShouldBe(siteId);
        calendar.TimeZone.ShouldBe(SiteTimeZone.Of(expectedIanaId));
    }

    [Fact]
    public void ChangingTheZoneInTheModel_ChangesTheProductionDay_WithNoCodeChange()
    {
        // The property that makes the factory model the single source of truth. The same instant is
        // filed under two different production days at two plants because their clocks read
        // differently — and nothing in Nvm.Time knows either plant exists.
        var active = new InMemoryActiveFactoryModel();
        ActivateBoth(active, Seed);

        var calendar = new ProductionCalendar(
            new FactoryModelSiteCalendarDirectory(active),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        // 2026-08-25T23:30Z is 06:30 on the 26th in Hai Phong — shift A of production day 26 — and
        // 01:30 on the 26th in Leipzig, still shift C of production day 25.
        var instant = new DateTimeOffset(2026, 8, 25, 23, 30, 0, TimeSpan.Zero);

        calendar.GetProductionDay(instant, "NV1").ShouldBe(ProductionDay.On(2026, 8, 26));
        calendar.GetShift(instant, "NV1").ShouldBe(Shift.A);

        calendar.GetProductionDay(instant, "DE1").ShouldBe(ProductionDay.On(2026, 8, 25));
        calendar.GetShift(instant, "DE1").ShouldBe(Shift.C);
    }

    [Fact]
    public void MovingAPlantToARevisionWithADifferentZone_MovesItsProductionDay()
    {
        // Editing the seed file in a test would edit a published revision, which is the one thing a
        // revision may never be. So the plant is moved to a document that says something different —
        // the same operation a real re-homing would be — and the calendar follows without a rebuild.
        var active = new InMemoryActiveFactoryModel();
        var haiPhong = Seed.FindSite("NV1").ShouldNotBeNull();
        var instant = new DateTimeOffset(2026, 8, 25, 23, 30, 0, TimeSpan.Zero);
        var calendar = new ProductionCalendar(
            new FactoryModelSiteCalendarDirectory(active),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        active.TryActivate(new ActiveFactoryModelRevision(Seed.Revision, haiPhong), null).ShouldBeTrue();
        calendar.GetProductionDay(instant, "NV1").ShouldBe(ProductionDay.On(2026, 8, 26));

        var moved = haiPhong with { TimeZoneId = "Europe/Berlin" };
        active.TryActivate(
            new ActiveFactoryModelRevision(Seed.Revision + 1, moved), Seed.Revision).ShouldBeTrue();

        calendar.GetProductionDay(instant, "NV1").ShouldBe(ProductionDay.On(2026, 8, 25));
        calendar.GetShift(instant, "NV1").ShouldBe(Shift.C);
    }

    [Fact]
    public void TheZoneComesFromTheRevisionInForce_NotTheNewestOnTheShelf()
    {
        // ADR-024: a staged rollout is normal. NV1 sitting on revision 3 while DE1 is still on 1 must
        // not make DE1 answer from revision 3 — the calendar reads what the plant is running.
        var active = new InMemoryActiveFactoryModel();
        var leipzig = Seed.FindSite("DE1").ShouldNotBeNull();
        var directory = new FactoryModelSiteCalendarDirectory(active);

        active.TryActivate(new ActiveFactoryModelRevision(1, leipzig with { TimeZoneId = "UTC" }), null)
            .ShouldBeTrue();

        directory.Find("DE1").ShouldNotBeNull().TimeZone.ShouldBe(SiteTimeZone.Of("UTC"));
    }

    [Fact]
    public void APlantWithNoActivatedRevision_HasNoCalendar()
    {
        // Not a bug and not a default. A plant nobody has switched on has no shifts to report against,
        // and answering "UTC, three eight-hour shifts" would be an invented fact.
        var directory = new FactoryModelSiteCalendarDirectory(new InMemoryActiveFactoryModel());

        directory.Find("NV1").ShouldBeNull();
    }

    [Fact]
    public void AnUnknownPlant_MakesTheCalendarThrowAndNameIt()
    {
        var active = new InMemoryActiveFactoryModel();
        ActivateBoth(active, Seed);

        var calendar = new ProductionCalendar(
            new FactoryModelSiteCalendarDirectory(active),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        var thrown = Should.Throw<UnknownSiteException>(
            () => calendar.GetProductionDay(DateTimeOffset.UnixEpoch, "US1"));

        thrown.SiteId.ShouldBe("US1");
    }

    private static FactoryModelSiteCalendarDirectory DirectoryOver(FactoryModelSnapshot seed, string siteId)
    {
        var active = new InMemoryActiveFactoryModel();
        var site = seed.FindSite(siteId).ShouldNotBeNull();

        active.TryActivate(new ActiveFactoryModelRevision(seed.Revision, site), null).ShouldBeTrue();

        return new FactoryModelSiteCalendarDirectory(active);
    }

    private static void ActivateBoth(InMemoryActiveFactoryModel active, FactoryModelSnapshot seed)
    {
        foreach (var site in seed.Sites)
        {
            active.TryActivate(new ActiveFactoryModelRevision(seed.Revision, site), null).ShouldBeTrue();
        }
    }
}
