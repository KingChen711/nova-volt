using Nvm.Material.Entities;

namespace Nvm.UnitTests.Material;

public sealed class MaterialRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    private static MaterialLot Lot(decimal remaining = 10m, DateTimeOffset? expires = null, int? maxExposure = null,
        DateTimeOffset? opened = null, LotQuality quality = LotQuality.Released) =>
        new("LOT-1", "ELECTROLYTE", remaining, "kg", Now.AddDays(-5), expires, maxExposure, opened, quality, 3);

    private static MaterialOverride Override(string rule, DateTimeOffset until) => new("OV-1", "LOT-1", rule, until);

    [Fact]
    public void ReleasedLotWithinAllLimits_Passes()
    {
        MaterialRules.Check(Lot(expires: Now.AddDays(1), maxExposure: 60, opened: Now.AddMinutes(-60)), 10m, "kg", Now, null, [])
            .ShouldBeNull();   // đúng biên: dùng hết số lượng, mở đúng 60 phút
    }

    [Fact]
    public void UomMismatch_IsCheckedFirst_AndCannotBeOverridden()
    {
        var violation = MaterialRules.Check(Lot(quality: LotQuality.Pending), 100m, "g", Now, null,
            [Override(MaterialRules.UomMismatch, Now.AddDays(1))]);
        violation!.Rule.ShouldBe(MaterialRules.UomMismatch);
        violation.Text.ShouldBe("Đơn vị g không khớp đơn vị của lot (kg).");
        MaterialRules.Overridable.ShouldNotContain(MaterialRules.UomMismatch);
    }

    [Theory]
    [InlineData(LotQuality.Pending)]
    [InlineData(LotQuality.Rejected)]
    public void LotNotReleased_IsRejected(LotQuality quality)
    {
        var violation = MaterialRules.Check(Lot(quality: quality), 1m, "kg", Now, null, []);
        violation!.Rule.ShouldBe(MaterialRules.NotReleased);
        violation.Text.ShouldBe("Lot chưa được Quality cho phép dùng.");
    }

    [Fact]
    public void QuantityAboveRemaining_IsRejected_AndCannotBeOverridden()
    {
        var violation = MaterialRules.Check(Lot(remaining: 2.5m), 2.501m, "kg", Now, null,
            [Override(MaterialRules.Insufficient, Now.AddDays(1))]);
        violation!.Rule.ShouldBe(MaterialRules.Insufficient);
        violation.Text.ShouldBe("Lot chỉ còn 2.5 kg, cần 2.501.");
    }

    [Fact]
    public void ExpiredLot_IsRejectedAtTheExpiryInstant_UnlessAValidOverrideExists()
    {
        var lot = Lot(expires: Now);
        var violation = MaterialRules.Check(lot, 1m, "kg", Now, null, []);
        violation!.Rule.ShouldBe(MaterialRules.Expired);
        violation.Text.ShouldBe("Lot hết hạn lúc 2026-09-20 08:00 UTC.");
        MaterialRules.Check(lot, 1m, "kg", Now, null, [Override(MaterialRules.Expired, Now.AddMinutes(1))]).ShouldBeNull();
        // Override hết hạn đúng lúc này hoặc cho luật khác thì không có tác dụng.
        MaterialRules.Check(lot, 1m, "kg", Now, null, [Override(MaterialRules.Expired, Now)])!.Rule.ShouldBe(MaterialRules.Expired);
        MaterialRules.Check(lot, 1m, "kg", Now, null, [Override(MaterialRules.Fifo, Now.AddDays(1))])!.Rule
            .ShouldBe(MaterialRules.Expired);
        MaterialRules.Check(Lot(expires: Now.AddTicks(1)), 1m, "kg", Now, null, []).ShouldBeNull();
    }

    [Fact]
    public void ExposureBeyondLimit_IsRejected_WithOperatorReadableDurations()
    {
        var lot = Lot(maxExposure: 240, opened: Now.AddMinutes(-272));
        var violation = MaterialRules.Check(lot, 1m, "kg", Now, null, []);
        violation!.Rule.ShouldBe(MaterialRules.ExposureExceeded);
        violation.Text.ShouldBe("Lot đã mở 4h32m / giới hạn 4h00m.");
        MaterialRules.Check(lot, 1m, "kg", Now, null, [Override(MaterialRules.ExposureExceeded, Now.AddHours(1))]).ShouldBeNull();
        // Chưa mở, hoặc không có giới hạn phơi nhiễm: không áp dụng.
        MaterialRules.Check(Lot(maxExposure: 1), 1m, "kg", Now, null, []).ShouldBeNull();
        MaterialRules.Check(Lot(opened: Now.AddDays(-30)), 1m, "kg", Now, null, []).ShouldBeNull();
    }

    [Fact]
    public void OlderAvailableLot_ViolatesFifo_UnlessOverridden()
    {
        var older = Lot() with { LotId = "LOT-0" };
        var violation = MaterialRules.Check(Lot(), 1m, "kg", Now, older, []);
        violation!.Rule.ShouldBe(MaterialRules.Fifo);
        violation.Text.ShouldBe("Còn lot cũ hơn (LOT-0) phải dùng trước (FIFO).");
        MaterialRules.Check(Lot(), 1m, "kg", Now, older, [Override(MaterialRules.Fifo, Now.AddHours(1))]).ShouldBeNull();
    }

    [Fact]
    public void Duration_PrintsWholeHoursAndTwoDigitMinutes()
    {
        MaterialRules.Duration(TimeSpan.FromMinutes(5)).ShouldBe("0h05m");
        MaterialRules.Duration(TimeSpan.FromHours(26) + TimeSpan.FromMinutes(59)).ShouldBe("26h59m");
    }

    [Fact]
    public void Check_RejectsNullArguments()
    {
        Should.Throw<ArgumentNullException>(() => MaterialRules.Check(null!, 1m, "kg", Now, null, []));
        Should.Throw<ArgumentNullException>(() => MaterialRules.Check(Lot(), 1m, "kg", Now, null, null!));
    }
}
