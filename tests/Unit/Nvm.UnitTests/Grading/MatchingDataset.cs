using System.Globalization;
using Nvm.Grading.Matching;

namespace Nvm.UnitTests.Grading;

/// <summary>
/// Bộ dữ liệu tất định cho matching (ví dụ tự dựng, không phải số đo nhà máy): capacity ~ N(120; 0,9) Ah chia bin
/// 0,5 Ah, OCV ~ N(3650; 4) mV có lệch theo lot, DCIR ~ N(0,90; 0,05) mΩ, mỗi cuộn cathode 1.000 cell liên tiếp,
/// thời điểm grade trải trong 20 ngày.
/// </summary>
public static class MatchingDataset
{
    public static readonly DateTimeOffset Now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    public static List<MatchCell> Create(int count, int seed = 42, string site = "NV1")
    {
        var random = new Random(seed);
        var lotOffset = new Dictionary<int, double>();
        var cells = new List<MatchCell>(count);
        for (var i = 0; i < count; i++)
        {
            var lot = i / 1000;
            if (!lotOffset.TryGetValue(lot, out var offset))
            { lotOffset[lot] = offset = Normal(random, 0, 2); }
            var capacity = Math.Round(Normal(random, 120.0, 0.9), 3);
            if (capacity is < 118.0 or >= 122.0)
            { continue; }
            var bin = "A" + ((int)((capacity - 118.0) / 0.5) + 1).ToString(CultureInfo.InvariantCulture);
            cells.Add(new MatchCell(
                string.Create(CultureInfo.InvariantCulture, $"{site}CL16{244 + i / 30_000:D3}{"ABC"[i / 10_000 % 3]}{i % 10_000 + 1:D5}")[..16],
                bin, (decimal)capacity, (decimal)Math.Round(Normal(random, 3650 + offset, 4), 2),
                (decimal)Math.Round(Normal(random, 0.90, 0.05), 3), "ROLL-" + lot.ToString("D4", CultureInfo.InvariantCulture),
                Now.AddHours(-random.Next(0, 20 * 24))));
        }
        return cells;
    }

    private static double Normal(Random random, double mean, double deviation) =>
        mean + deviation * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
