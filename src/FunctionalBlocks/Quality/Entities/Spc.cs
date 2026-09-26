namespace Nvm.Quality.Entities;

/// <summary>Một subgroup đo (ví dụ 5 mẫu coating weight lấy cùng lúc) và thống kê của nó.</summary>
public sealed record SpcSubgroup(string SubgroupId, DateTimeOffset TakenAt, IReadOnlyList<decimal> Values)
{
    public decimal Mean => Values.Average();
    public decimal Range => Values.Max() - Values.Min();
}

/// <summary>Giới hạn kiểm soát của biểu đồ X̄-R và năng lực quá trình.</summary>
public sealed record XbarRChart(int SubgroupSize, decimal GrandMean, decimal MeanRange, decimal XbarUcl, decimal XbarLcl,
    decimal RangeUcl, decimal RangeLcl, decimal Sigma, decimal? Cpk, IReadOnlyList<string> OutOfControl);

/// <summary>
/// X̄-R theo hằng số chuẩn A2/D3/D4/d2 (n = 2..10). Sigma ước lượng bằng R̄/d2; Cpk = min(USL − X̿, X̿ − LSL)/(3σ).
/// Điểm ngoài giới hạn chỉ theo quy tắc 1 (ngoài 3σ) — các quy tắc Western Electric khác chưa cài.
/// </summary>
public static class XbarR
{
    private static readonly Dictionary<int, (decimal A2, decimal D3, decimal D4, decimal D2)> Constants = new()
    {
        [2] = (1.880m, 0m, 3.267m, 1.128m),
        [3] = (1.023m, 0m, 2.574m, 1.693m),
        [4] = (0.729m, 0m, 2.282m, 2.059m),
        [5] = (0.577m, 0m, 2.114m, 2.326m),
        [6] = (0.483m, 0m, 2.004m, 2.534m),
        [7] = (0.419m, 0.076m, 1.924m, 2.704m),
        [8] = (0.373m, 0.136m, 1.864m, 2.847m),
        [9] = (0.337m, 0.184m, 1.816m, 2.970m),
        [10] = (0.308m, 0.223m, 1.777m, 3.078m),
    };

    public static XbarRChart Compute(IReadOnlyList<SpcSubgroup> subgroups, decimal? lowerSpec, decimal? upperSpec)
    {
        ArgumentNullException.ThrowIfNull(subgroups);
        if (subgroups.Count < 2)
        { throw new ArgumentException("X̄-R cần ít nhất hai subgroup.", nameof(subgroups)); }
        var size = subgroups[0].Values.Count;
        if (subgroups.Any(g => g.Values.Count != size) || !Constants.TryGetValue(size, out var c))
        { throw new ArgumentException("Mọi subgroup phải cùng cỡ mẫu 2–10.", nameof(subgroups)); }
        var grandMean = subgroups.Average(g => g.Mean);
        var meanRange = subgroups.Average(g => g.Range);
        var sigma = meanRange / c.D2;
        var chart = new XbarRChart(size, grandMean, meanRange, grandMean + c.A2 * meanRange, grandMean - c.A2 * meanRange,
            c.D4 * meanRange, c.D3 * meanRange, sigma, Cpk(grandMean, sigma, lowerSpec, upperSpec), []);
        var outside = subgroups.Where(g => g.Mean > chart.XbarUcl || g.Mean < chart.XbarLcl ||
            g.Range > chart.RangeUcl || g.Range < chart.RangeLcl).Select(g => g.SubgroupId).ToList();
        return chart with { OutOfControl = outside };
    }

    private static decimal? Cpk(decimal mean, decimal sigma, decimal? lower, decimal? upper)
    {
        if (sigma == 0 || (lower is null && upper is null))
        { return null; }
        var sides = new List<decimal>();
        if (upper is { } usl)
        { sides.Add((usl - mean) / (3 * sigma)); }
        if (lower is { } lsl)
        { sides.Add((mean - lsl) / (3 * sigma)); }
        return Math.Round(sides.Min(), 3);
    }
}
