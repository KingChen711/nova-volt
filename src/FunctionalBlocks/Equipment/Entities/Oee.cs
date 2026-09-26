namespace Nvm.Equipment.Entities;

/// <summary>Một đoạn dừng, đã phân loại.</summary>
public sealed record DowntimeInterval(DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Category);

/// <summary>Sản lượng một khoảng, kèm ideal cycle đã chốt lúc ghi.</summary>
public sealed record ProductionCount(DateTimeOffset WindowFrom, DateTimeOffset WindowTo, long TotalCount, long GoodCount,
    int IdealCycleMilliseconds);

/// <summary>
/// Các base của OEE (giây và số đếm). Tỉ lệ luôn được tính từ base; gộp nhiều máy/line là cộng base rồi tính lại,
/// không bao giờ lấy trung bình các phần trăm (scope §6.10).
/// </summary>
public sealed record OeeBase(decimal PlannedSeconds, decimal UnplannedDowntimeSeconds, decimal MicroStopSeconds,
    decimal IdealSeconds, long TotalCount, long GoodCount)
{
    public static readonly OeeBase Zero = new(0m, 0m, 0m, 0m, 0, 0);

    /// <summary>Thời gian chạy = thời gian kế hoạch trừ dừng không kế hoạch; micro-stop vẫn nằm trong thời gian chạy.</summary>
    public decimal RunSeconds => PlannedSeconds - UnplannedDowntimeSeconds;

    public decimal? Availability => PlannedSeconds > 0 ? RunSeconds / PlannedSeconds : null;

    public decimal? Performance => RunSeconds > 0 ? IdealSeconds / RunSeconds : null;

    public decimal? Quality => TotalCount > 0 ? (decimal)GoodCount / TotalCount : null;

    /// <summary>A × P × Q. Tính thẳng bằng thời gian lý tưởng của hàng tốt chia thời gian kế hoạch để không mất độ chính xác.</summary>
    public decimal? Oee => PlannedSeconds > 0 && TotalCount > 0
        ? IdealSeconds * GoodCount / TotalCount / PlannedSeconds
        : null;

    public OeeBase Add(OeeBase other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(PlannedSeconds + other.PlannedSeconds, UnplannedDowntimeSeconds + other.UnplannedDowntimeSeconds,
            MicroStopSeconds + other.MicroStopSeconds, IdealSeconds + other.IdealSeconds, TotalCount + other.TotalCount,
            GoodCount + other.GoodCount);
    }

    public static OeeBase Combine(IEnumerable<OeeBase> parts) => parts.Aggregate(Zero, (sum, part) => sum.Add(part));
}

public static class OeeCalculator
{
    /// <summary>
    /// Base OEE của một máy trong [<paramref name="from"/>, <paramref name="to"/>). Dừng được cắt theo cửa sổ.
    /// Sản lượng thuộc cửa sổ khi <c>WindowTo</c> nằm trong (from, to]: máy báo đếm theo khoảng ngắn nên sai lệch
    /// chỉ ở một khoảng biên.
    /// </summary>
    public static OeeBase For(DateTimeOffset from, DateTimeOffset to, IEnumerable<DowntimeInterval> downtimes,
        IEnumerable<ProductionCount> counts)
    {
        ArgumentNullException.ThrowIfNull(downtimes);
        ArgumentNullException.ThrowIfNull(counts);
        if (to <= from)
        { return OeeBase.Zero; }
        decimal planned = 0m, unplanned = 0m, micro = 0m;
        foreach (var downtime in downtimes)
        {
            var start = downtime.StartedAt > from ? downtime.StartedAt : from;
            var end = downtime.EndedAt < to ? downtime.EndedAt : to;
            if (end <= start)
            { continue; }
            var seconds = Seconds(end - start);
            switch (downtime.Category)
            {
                case DowntimeCategories.Planned:
                    planned += seconds;
                    break;
                case DowntimeCategories.Unplanned:
                    unplanned += seconds;
                    break;
                default:
                    micro += seconds;
                    break;
            }
        }
        decimal ideal = 0m;
        long total = 0, good = 0;
        foreach (var count in counts.Where(c => c.WindowTo > from && c.WindowTo <= to))
        {
            ideal += count.TotalCount * (decimal)count.IdealCycleMilliseconds / 1000m;
            total += count.TotalCount;
            good += count.GoodCount;
        }
        return new OeeBase(Seconds(to - from) - planned, unplanned, micro, ideal, total, good);
    }

    private static decimal Seconds(TimeSpan value) => value.Ticks / (decimal)TimeSpan.TicksPerSecond;
}
