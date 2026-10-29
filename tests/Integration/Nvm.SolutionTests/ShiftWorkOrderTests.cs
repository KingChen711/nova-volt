using Nvm.Product;
using Nvm.Project;

namespace Nvm.SolutionTests;

/// <summary>DoD R4: WO theo ca qua timer đầu ca, mô phỏng bằng giờ giả.</summary>
public sealed class ShiftWorkOrderTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Three_days_of_shifts_open_one_work_order_per_default_freeze_the_previous_shift_and_complete_after_the_bop_hours()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        var mondayMorning = Monday.AddHours(7);

        for (var shift = 0; shift < 6; shift++)
        {
            var start = Monday.AddHours(7 + (12 * shift));
            if (shift == 3)
            {
                // Ca sáng thứ Hai đóng băng lúc 19:00; một giờ trước 19:00 thứ Ba vẫn Frozen, đúng 19:00 thì Complete (24 giờ).
                h.RunClockUntil(start.AddHours(-1));
                h.Read<WorkOrder>(q => q.Where(w => w.EstimatedStart == mondayMorning)).ShouldAllBe(w => w.Status == NvWorkOrderStates.Frozen);
            }

            h.RunClockUntil(start);

            var active = h.Read<WorkOrder>(q => q.Where(w => w.Status == WorkOrderStates.Active));
            active.Count.ShouldBe(20, $"shift {shift}");
            active.ShouldAllBe(w => w.EstimatedStart == start && w.EstimatedEnd == start.AddHours(12));
            h.Read<WorkOrder>(q => q.Where(w => w.EstimatedEnd == start && w.Status == WorkOrderStates.Active)).ShouldBeEmpty();
            if (shift > 0)
            {
                // WO của ca trước bị đóng băng đúng lúc ca này bắt đầu.
                var previous = h.Read<WorkOrder>(q => q.Where(w => w.EstimatedEnd == start));
                previous.Count.ShouldBe(20);
                previous.ShouldAllBe(w => w.Status == NvWorkOrderStates.Frozen);
            }
        }

        h.RunClockUntil(Monday.AddDays(3).AddHours(6));
        var all = h.Read<WorkOrder>();
        all.Count.ShouldBe(120);
        all.Count(w => w.Status == WorkOrderStates.Complete).ShouldBe(48);
        all.Count(w => w.Status == NvWorkOrderStates.Frozen).ShouldBe(52);
        all.Count(w => w.Status == WorkOrderStates.Active).ShouldBe(20);
        all.Where(w => w.ProcessNId!.StartsWith("Form_", StringComparison.Ordinal)).ShouldNotContain(w => w.Status == WorkOrderStates.Complete);
        all.Where(w => w.EstimatedStart == mondayMorning).Where(w => !w.ProcessNId!.StartsWith("Form_", StringComparison.Ordinal))
            .ShouldAllBe(w => w.Status == WorkOrderStates.Complete);

        var names = all.Where(w => w.EstimatedStart == mondayMorning).Select(w => w.NId).Order().ToList();
        names.ShouldContain("L1Coat202610260700");
        names.ShouldContain("L4Module202610260700");
    }

    [Theory]
    [InlineData(2026, 10, 31)]
    [InlineData(2027, 3, 13)]
    public async Task Shift_windows_stay_contiguous_across_a_daylight_saving_change(int year, int month, int day)
    {
        // Server chạy UTC (mặc định): ngày đổi giờ ở Mỹ không làm lệch timer hay khung ca; mỗi ca vẫn nối liền ca trước.
        var before = new DateTimeOffset(year, month, day, 6, 0, 0, TimeSpan.Zero);
        var h = await SolutionHarness.WithMasterDataAsync(before);

        h.RunClockUntil(before.AddDays(2));

        var coat = h.Read<WorkOrder>(q => q.Where(w => w.ProcessNId == "Coat_LN1").OrderBy(w => w.EstimatedStart));
        coat.Count.ShouldBe(4);
        coat.Zip(coat.Skip(1)).ShouldAllBe(pair => pair.First.EstimatedEnd == pair.Second.EstimatedStart);
        coat.Select(w => w.EstimatedStart!.Value.UtcDateTime.Hour).ShouldBe([7, 19, 7, 19]);
    }
}
