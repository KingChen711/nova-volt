using Microsoft.Extensions.Time.Testing;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;
using Nvm.FactoryModel.Time;
using Nvm.Time;

namespace Nvm.UnitTests.Time;

public sealed class FactoryModelSiteCalendarDirectoryTests
{
    // Seed thật, không phải fixture: toàn bộ khẳng định của commit này là zone đến từ chính document
    // mà một nhà máy đang thực sự chạy.
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
        // Tính chất khiến factory model trở thành single source of truth. Cùng một thời điểm được ghi
        // nhận dưới hai production day khác nhau ở hai nhà máy vì đồng hồ của chúng đọc khác nhau —
        // và không có gì trong Nvm.Time biết bất kỳ nhà máy nào tồn tại cả.
        var active = new InMemoryActiveFactoryModel();
        ActivateBoth(active, Seed);

        var calendar = new ProductionCalendar(
            new FactoryModelSiteCalendarDirectory(active),
            new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        // 2026-08-25T23:30Z là 06:30 ngày 26 ở Hải Phòng — shift A của production day 26 — và là
        // 01:30 ngày 26 ở Leipzig, vẫn còn thuộc shift C của production day 25.
        var instant = new DateTimeOffset(2026, 8, 25, 23, 30, 0, TimeSpan.Zero);

        calendar.GetProductionDay(instant, "NV1").ShouldBe(ProductionDay.On(2026, 8, 26));
        calendar.GetShift(instant, "NV1").ShouldBe(Shift.A);

        calendar.GetProductionDay(instant, "DE1").ShouldBe(ProductionDay.On(2026, 8, 25));
        calendar.GetShift(instant, "DE1").ShouldBe(Shift.C);
    }

    [Fact]
    public void MovingAPlantToARevisionWithADifferentZone_MovesItsProductionDay()
    {
        // Sửa file seed ngay trong một test tức là sửa một revision đã được publish, mà đó chính là
        // điều một revision không bao giờ được phép là. Nên nhà máy được chuyển sang một document nói
        // một điều khác — cùng thao tác như một lần re-homing thật sự — và calendar đi theo mà không
        // cần rebuild.
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
        // ADR-024: một staged rollout là bình thường. NV1 đang ở revision 3 trong khi DE1 vẫn ở
        // revision 1 không được phép khiến DE1 trả lời theo revision 3 — calendar đọc theo cái mà
        // nhà máy đang thực sự chạy.
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
        // Không phải bug và cũng không phải giá trị mặc định. Một nhà máy chưa ai bật lên thì không
        // có shift nào để báo cáo theo, và trả lời "UTC, ba shift tám giờ" sẽ là một sự kiện bịa đặt.
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
