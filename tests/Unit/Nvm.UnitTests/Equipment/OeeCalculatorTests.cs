using Nvm.Equipment.Entities;

namespace Nvm.UnitTests.Equipment;

public sealed class OeeCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 12, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(4.99, DowntimeCategories.Unplanned, DowntimeCategories.MicroStop)]
    [InlineData(5.00, DowntimeCategories.Unplanned, DowntimeCategories.Unplanned)]
    [InlineData(0.50, DowntimeCategories.Planned, DowntimeCategories.Planned)]
    [InlineData(90.0, DowntimeCategories.Planned, DowntimeCategories.Planned)]
    public void Classify_ShortUnplannedStopsAreMicroStops_PlannedStaysPlanned(double minutes, string reason, string expected) =>
        DowntimeRules.Classify(reason, TimeSpan.FromMinutes(minutes)).ShouldBe(expected);

    [Fact]
    public void Window_ClipsDowntimeAtBothEdges_AndCountsByWindowEnd()
    {
        var result = OeeCalculator.For(T0, T0.AddHours(1),
            [
                new DowntimeInterval(T0.AddMinutes(-30), T0.AddMinutes(10), DowntimeCategories.Planned),     // 10 phút trong cửa sổ
                new DowntimeInterval(T0.AddMinutes(50), T0.AddMinutes(80), DowntimeCategories.Unplanned),    // 10 phút trong cửa sổ
                new DowntimeInterval(T0.AddMinutes(20), T0.AddMinutes(22), DowntimeCategories.MicroStop),
                new DowntimeInterval(T0.AddHours(2), T0.AddHours(3), DowntimeCategories.Unplanned),         // ngoài cửa sổ
            ],
            [
                new ProductionCount(T0.AddMinutes(-15), T0, 999, 999, 1000),                                   // kết thúc đúng lúc bắt đầu: không tính
                new ProductionCount(T0, T0.AddMinutes(30), 900, 890, 1000),
                new ProductionCount(T0.AddMinutes(30), T0.AddHours(1), 600, 600, 2000),
            ]);
        result.PlannedSeconds.ShouldBe(3000m);
        result.UnplannedDowntimeSeconds.ShouldBe(600m);
        result.MicroStopSeconds.ShouldBe(120m);
        result.IdealSeconds.ShouldBe(2100m);
        result.TotalCount.ShouldBe(1500);
        result.GoodCount.ShouldBe(1490);
        result.Availability.ShouldBe(2400m / 3000m);
        result.Performance.ShouldBe(2100m / 2400m);
        // A × P × Q phải khớp công thức rút gọn.
        (result.Availability!.Value * result.Performance!.Value * result.Quality!.Value).ShouldBe(result.Oee!.Value, 0.0000000001m);
    }

    [Fact]
    public void Combining_SumsBases_NeverAveragesPercentages()
    {
        var longLine = new OeeBase(25_200m, 1_800m, 180m, 20_000m, 20_000, 19_000);
        var shortLine = new OeeBase(7_200m, 0m, 0m, 7_000m, 7_000, 6_930);
        var combined = OeeBase.Combine([longLine, shortLine]);
        combined.Oee!.Value.ShouldBe(25_930m / 32_400m, 0.0000000001m);
        var average = (longLine.Oee!.Value + shortLine.Oee!.Value) / 2;
        Math.Abs(combined.Oee!.Value - average).ShouldBeGreaterThan(0.05m);
    }

    [Fact]
    public void EmptyWindow_HasNoRatios()
    {
        var result = OeeCalculator.For(T0, T0, [], []);
        result.Oee.ShouldBeNull();
        result.Availability.ShouldBeNull();
    }
}
