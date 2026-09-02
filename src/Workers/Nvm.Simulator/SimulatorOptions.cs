using Nvm.Simulator.Faults;

namespace Nvm.Simulator;

/// <summary>Nhà máy chạy nhanh cỡ nào, và process này đang giả làm phần nào của nó.</summary>
/// <remarks>
/// <para>
/// Hai con số quan trọng nhất là <see cref="SamplePeriod"/> và <see cref="TimeCompression"/>, và
/// chúng làm hai việc khác nhau. Sample period được đo theo <b>process time</b> và quyết định một
/// cycle tạo ra bao nhiêu measurement; compression được đo theo <b>wall clock</b> và quyết định run
/// chạy hết bao lâu. Thay đổi compression không được phép làm thay đổi dù chỉ một measurement, đây
/// chính là tính chất mà <c>SimulatorWorkerTests</c> tồn tại để giữ vững.
/// </para>
/// <para>
/// Compression là một hệ số nhân trên đồng hồ, không phải một khoảng sleep ngắn hơn. Một formation
/// cycle dài mười tám giờ và một test không thể chờ mười tám giờ, nhưng nó cũng không được phép bỏ
/// qua sample để đi tắt tới đó — số lượng sample chính là vế bên trái của reconciliation ở D1.
/// </para>
/// </remarks>
public sealed class SimulatorOptions
{
    /// <summary>Line mà process này đại diện phát ngôn.</summary>
    public string LinePath { get; set; } = "NOVAVOLT/NV1/FORMATION/F1";

    /// <summary>Danh sách channel được đọc từ model revision nào. Null nghĩa là revision mới nhất.</summary>
    /// <remarks>
    /// Nhà máy là thứ quyết định channel nào tồn tại. Tự bịa ra danh sách sẽ tạo ra các topic mà
    /// ingestion từ chối (K3), và một simulator mà mọi message của nó đều bị từ chối thì chẳng đo
    /// được gì.
    /// </remarks>
    public int? Revision { get; set; }

    /// <summary>Thư mục chứa các document <c>factory-model.r*.json</c>.</summary>
    public string SeedDirectory { get; set; } = "seed";

    /// <summary>Một cell ở trong channel bao lâu.</summary>
    public TimeSpan CycleDuration { get; set; } = TimeSpan.FromHours(18);

    /// <summary>Có bao nhiêu process time trôi qua giữa hai lần lấy sample của một channel.</summary>
    public TimeSpan SamplePeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Có bao nhiêu giây thời gian nhà máy trôi qua trên mỗi giây wall clock.</summary>
    public double TimeCompression { get; set; } = 1000;

    /// <summary>Host của MQTT broker. Trên <c>ot-net</c> đây là tên service của EMQX.</summary>
    public string BrokerHost { get; set; } = "emqx";

    /// <summary>Port của MQTT broker.</summary>
    public int BrokerPort { get; set; } = 1883;

    /// <summary>Session number mà run này publish dưới đó, trong <c>bdSeq</c>.</summary>
    public ulong BirthDeathSequence { get; set; }

    /// <summary>Nhà máy này bị yêu cầu hành xử sai đến mức nào. Mặc định mọi thứ đều tắt.</summary>
    public SimulatorFaults Faults { get; set; } = new();

    /// <summary>Nơi run report được ghi ra — vế bên trái của reconciliation ở D1.</summary>
    public string ReportPath { get; set; } = "reports/simulator-run.json";

    /// <summary>Run report được ghi lại thường xuyên cỡ nào.</summary>
    /// <remarks>
    /// Được đo theo wall clock, không theo process time. File này tồn tại để một run có thể được
    /// reconcile trong khi nó vẫn đang chạy và cả sau khi nó đã bị kill, và cả hai đều là câu hỏi về
    /// những phút giây thực sự.
    /// </remarks>
    public TimeSpan ReportInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Đợi bao lâu trước khi quay số kết nối lại broker sau khi connection bị rớt.</summary>
    /// <remarks>
    /// Một nhà máy không dừng lại chỉ vì broker restart (N15), nên việc mất connection phải là một
    /// khoảng tạm dừng chứ không phải là kết thúc của một run. Hai giây khớp với delay reconnect của
    /// chính gateway: đủ dài để một lần broker restart không bị đáp lại bằng một cơn bão retry, đủ
    /// ngắn để node quay lại trước khi một consumer coi nó là stale.
    /// </remarks>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Wall clock chờ bao lâu giữa hai tick.</summary>
    /// <exception cref="InvalidOperationException">Các setting không mô tả một nhà máy chạy được.</exception>
    public TimeSpan TickInterval => SamplePeriod / TimeCompression;

    /// <summary>Từ chối các setting không thể tạo ra một nhà máy, trước khi có bất kỳ kết nối nào.</summary>
    /// <exception cref="InvalidOperationException">Một giá trị nằm ngoài khoảng cho phép.</exception>
    public void Validate()
    {
        Faults.Validate();

        if (TimeCompression <= 0)
        {
            throw new InvalidOperationException("Time compression must be positive.");
        }

        if (SamplePeriod <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The sample period must be positive.");
        }

        // Một cycle không phải là bội số nguyên của sample thì vẫn hợp lệ nhưng gây bất ngờ: sample
        // cuối của một cell và sample đầu của cell kế tiếp sẽ gần nhau hơn so với phần còn lại. Bị từ
        // chối để reconciliation ở D1 có thể nhân lên thay vì phải giải thích.
        if (CycleDuration.Ticks % SamplePeriod.Ticks != 0)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"A cycle of {CycleDuration} is not a whole number of {SamplePeriod} samples."));
        }

        // Dưới độ phân giải của một system timer thì wall clock không theo kịp nữa, và run âm thầm
        // chạy chậm hơn so với những gì compression tuyên bố — điều này sẽ khiến một con số throughput
        // trở thành một lời nói dối.
        if (TickInterval < TimeSpan.FromMilliseconds(1))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"A sample period of {SamplePeriod} compressed {TimeCompression}x leaves {TickInterval.TotalMilliseconds:0.###} ms per tick, which is below what a timer can hold. Lower the compression or lengthen the sample period."));
        }

        if (ReconnectDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Reconnect delay must be positive.");
        }

        if (ReportInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The run report interval must be positive; the reconciliation reads that file while the run is going.");
        }
    }
}
