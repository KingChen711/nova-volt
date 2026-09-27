using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nvm.Contracts.Events.Grading;
using Nvm.Grading.Entities;

namespace Nvm.UnitTests.Grading;

public sealed class GradingRuleSetTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    // Khoảng nửa mở [min, max). A ưu tiên hơn B; hai bin chồng ở dải 59–60 Ah.
    private static readonly GradingBin A = new("A", 60m, 62m, 4100m, 4200m, 10m, 20m, 1);
    private static readonly GradingBin B = new("B", 58m, 61m, 4100m, 4200m, 10m, 25m, 2);
    private static readonly GradingReject Drift = new("OCV_DRIFT", "OcvDriftMillivolt", 15m, false);
    private static readonly GradingReject LowCapacity = new("LOW_CAPACITY", "CapacityAh", 50m, true);

    private static GradingRuleSet Rules(ImmutableArray<GradingBin>? bins = null, ImmutableArray<GradingReject>? rejects = null) =>
        new("RS-CELL", 3, "NV-CELL-60AH", From, bins ?? [A, B], rejects ?? [Drift, LowCapacity]);

    private static GradingMeasurement M(decimal capacity = 60.5m, decimal ocv = 4150m, decimal dcir = 15m, decimal? drift = 2m) =>
        new(capacity, ocv, dcir, drift);

    [Fact]
    public void Evaluate_PicksTheFirstBinByPriority_WithHalfOpenRanges()
    {
        Rules().Evaluate(M()).ShouldBe(new GradeOutcome("A", null));
        Rules().Evaluate(M(capacity: 60m)).BinCode.ShouldBe("A");       // min thuộc khoảng
        Rules().Evaluate(M(capacity: 59.9m)).BinCode.ShouldBe("B");
        Rules().Evaluate(M(capacity: 62m)).ShouldBe(new GradeOutcome(null, GradingRuleSet.NoBin));   // max không thuộc
        Rules().Evaluate(M(dcir: 20m)).BinCode.ShouldBe("B");           // ra khỏi A theo DCIR, B còn chứa
        Rules().Evaluate(M(dcir: 9.99m)).RejectCode.ShouldBe(GradingRuleSet.NoBin);
        Rules().Evaluate(M(ocv: 4200m)).RejectCode.ShouldBe(GradingRuleSet.NoBin);
        Rules().Evaluate(M(ocv: 4099m)).RejectCode.ShouldBe(GradingRuleSet.NoBin);
        // Cùng priority: thứ tự theo mã bin, không theo thứ tự khai báo.
        Rules(bins: [B with { Priority = 1, BinCode = "Z" }, A]).Evaluate(M()).BinCode.ShouldBe("A");
    }

    [Fact]
    public void Rejects_ComeBeforeBins_InBothDirections_AndSkipMissingValues()
    {
        Rules().Evaluate(M(drift: 15.01m)).ShouldBe(new GradeOutcome(null, "OCV_DRIFT"));
        Rules().Evaluate(M(drift: 15m)).BinCode.ShouldBe("A");                 // bằng ngưỡng chưa loại
        Rules().Evaluate(M(drift: null)).BinCode.ShouldBe("A");                // chưa đo drift: không loại theo drift
        Rules().Evaluate(M(capacity: 49.9m)).RejectCode.ShouldBe("LOW_CAPACITY");
        Rules().Evaluate(M(capacity: 50m)).RejectCode.ShouldBe(GradingRuleSet.NoBin);
        Rules(rejects: [new("HIGH_OCV", "OcvMillivolt", 4180m, false)]).Evaluate(M(ocv: 4181m)).RejectCode.ShouldBe("HIGH_OCV");
        Rules(rejects: [new("HIGH_DCIR", "DcirMilliOhm", 14m, false)]).Evaluate(M()).RejectCode.ShouldBe("HIGH_DCIR");
        Should.Throw<InvalidDataException>(() => Rules(rejects: [new("X", "Temperature", 1m, false)]).Evaluate(M()));
        Should.Throw<ArgumentNullException>(() => Rules().Evaluate(null!));
    }

    [Fact]
    public void Problems_ReportsEachConfigurationError()
    {
        Rules().Problems().ShouldBeEmpty();
        Rules(bins: []).Problems().ShouldBe(["Rule set phải có ít nhất một bin."]);
        (Rules() with { Bins = default }).Problems().ShouldBe(["Rule set phải có ít nhất một bin."]);
        Rules(bins: [A, A with { Priority = 5 }]).Problems().ShouldBe(["Mã bin bị trùng."]);
        Rules(bins: [A with { CapacityMaxAh = 60m }]).Problems().ShouldBe(["Mỗi khoảng của bin phải có min < max."]);
        Rules(bins: [A with { OcvMaxMillivolt = 4100m }]).Problems().ShouldBe(["Mỗi khoảng của bin phải có min < max."]);
        Rules(bins: [A with { DcirMaxMilliOhm = 9m }]).Problems().ShouldBe(["Mỗi khoảng của bin phải có min < max."]);
        Rules(rejects: [new("X", "Temperature", 1m, false)]).Problems().ShouldBe(["Tiêu chí loại dùng chỉ số không tồn tại."]);
        (Rules() with { Rejects = default }).Problems().ShouldBeEmpty();
        Rules(bins: [A, A with { CapacityMaxAh = 1m }], rejects: [new("X", "Temperature", 1m, false)]).Problems().Count.ShouldBe(3);
        GradingRuleSet.Metrics.ShouldBe(["CapacityAh", "OcvMillivolt", "DcirMilliOhm", "OcvDriftMillivolt"]);
    }

    [Fact]
    public void ContentHash_MatchesTheCanonicalForm_AndIgnoresDeclarationOrder()
    {
        var local = new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.FromHours(7));
        var set = Rules() with { EffectiveFrom = local };
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"RS-CELL|3|NV-CELL-60AH|2026-09-01T00:00:00.0000000+00:00|B:A:60:62:4100:4200:10:20:1|B:B:58:61:4100:4200:10:25:2|R:LOW_CAPACITY:CapacityAh:50:True|R:OCV_DRIFT:OcvDriftMillivolt:15:False");
        set.ContentSha256().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        Rules(bins: [B, A], rejects: [LowCapacity, Drift]).ContentSha256().ShouldBe(Rules().ContentSha256());
        (Rules() with { Rejects = default }).ContentSha256().ShouldBe(Rules(rejects: []).ContentSha256());
        Rules(bins: [A, B with { Priority = 3 }]).ContentSha256().ShouldNotBe(Rules().ContentSha256());
    }
}
