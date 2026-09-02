using System.Diagnostics.Metrics;

namespace Nvm.Ingestion;

/// <summary>Một reading mất bao lâu để đi từ device clock vào tới database.</summary>
/// <remarks>
/// <para>
/// <c>recorded_at − device_timestamp</c>, chính là con số của D2. Nó trải khắp toàn bộ đường đi —
/// buffering của chính device, MQTT, fsync của gateway, store-and-forward, HTTP, transaction — nên
/// đây là con số lag duy nhất trả lời được câu hỏi mà một operator thực sự hỏi: thứ mới nhất tôi
/// nhìn thấy được cũ tới mức nào.
/// </para>
/// <para>
/// <b>Chỉ những reading có đồng hồ tốt mới được lấy mẫu.</b> Phép đo lấy hiệu của hai đồng hồ, nên
/// một PLC lệch hai giờ sẽ đóng góp một lag hai giờ không nói lên điều gì về pipeline, và một PLC
/// nhanh hai giờ sẽ đóng góp một giá trị âm. Cả hai đều phá hỏng một percentile. Vì vậy harness của
/// C16 chạy với clock drift bị tắt, và sự thật đó phải đi kèm với mọi con số mà nó tạo ra — nếu
/// không ai đó ở M8 đọc bảng số và kết luận rằng lag từng có lúc âm.
/// </para>
/// </remarks>
public sealed class IngestionLag
{
    /// <summary>Percentile được tính trên bao nhiêu mẫu gần đây.</summary>
    /// <remarks>
    /// Một vòng ring, không phải toàn bộ run. D2 hỏi liệu hệ thống có <i>duy trì</i> được một tốc độ
    /// hay không, và một percentile lũy kế sẽ che giấu một pipeline đã tụt lại trong hai phút gần
    /// nhất dưới mười phút hành vi tốt trước đó.
    /// </remarks>
    public const int WindowSize = 8192;

    private static readonly Meter Meter = new("Nvm.Ingestion", "1.0.0");
    private static readonly Histogram<double> LagHistogram =
        Meter.CreateHistogram<double>("nvm.ingest.lag", unit: "s");

    private readonly double[] _samples = new double[WindowSize];
    private readonly Lock _gate = new();
    private long _total;
    private int _next;
    private int _filled;

    /// <summary>Ghi lại lag của một reading.</summary>
    /// <param name="lag">Khoảng thời gian giữa device clock và lúc commit.</param>
    public void Record(TimeSpan lag)
    {
        LagHistogram.Record(lag.TotalSeconds);

        lock (_gate)
        {
            _samples[_next] = lag.TotalSeconds;
            _next = (_next + 1) % WindowSize;
            _total++;

            if (_filled < WindowSize)
            {
                _filled++;
            }
        }
    }

    /// <summary>Các percentile mà D2 được đo dựa theo.</summary>
    public IngestionLagSnapshot Snapshot()
    {
        double[] window;

        lock (_gate)
        {
            if (_filled == 0)
            {
                return new IngestionLagSnapshot(0, 0, 0, 0, 0);
            }

            window = _samples[.._filled];
            window = [.. window];
        }

        Array.Sort(window);

        return new IngestionLagSnapshot(
            _total,
            Percentile(window, 0.50),
            Percentile(window, 0.95),
            Percentile(window, 0.99),
            window[^1]);
    }

    // Nearest-rank trên một bản sao đã sắp xếp. Nội suy giữa hai mẫu sẽ bịa ra một lag mà không
    // reading nào từng có, và threshold của D2 được so sánh với một phép đo, không phải với một model.
    private static double Percentile(double[] sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;

        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

/// <summary>Lag percentile trên window gần đây, tính bằng giây.</summary>
/// <param name="Samples">Tổng số reading đã lấy mẫu kể từ lúc process khởi động.</param>
/// <param name="P50">Lag trung vị.</param>
/// <param name="P95">Con số của D2: phải giữ dưới năm giây ở mức 5.000 msg/s.</param>
/// <param name="P99">Lag đuôi.</param>
/// <param name="Max">Lag tệ nhất trong window.</param>
public sealed record IngestionLagSnapshot(long Samples, double P50, double P95, double P99, double Max);
