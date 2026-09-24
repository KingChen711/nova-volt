using System.Diagnostics.Metrics;

namespace Nvm.Ingestion;

/// <summary>Bộ đếm cấp process, chỉ cập nhật sau khi database transaction commit.</summary>
public sealed class IngestionMetrics
{
    private static readonly Meter Meter = new("Nvm.Ingestion", "1.0.0");
    private static readonly Counter<long> InsertedCounter =
        Meter.CreateCounter<long>("nvm.ingest.inserted", unit: "{reading}");
    private static readonly Counter<long> DuplicateCounter =
        Meter.CreateCounter<long>("nvm.ingest.duplicates", unit: "{reading}");
    private static readonly Counter<long> DriftedCounter =
        Meter.CreateCounter<long>("nvm.ingest.drifted", unit: "{reading}");
    private static readonly Counter<long> RetentionRiskCounter =
        Meter.CreateCounter<long>("nvm.ingest.retention_risk", unit: "{reading}");
    private static readonly Counter<long> PublishFailureCounter =
        Meter.CreateCounter<long>("nvm.ingest.publish_failures", unit: "{event}");
    private static readonly Counter<long> PublishedCounter =
        Meter.CreateCounter<long>("nvm.ingest.published", unit: "{event}");
    private static readonly Counter<long> WriteRetryCounter =
        Meter.CreateCounter<long>("nvm.ingest.write_retries", unit: "{transaction}");

    private long _insertedCount;
    private long _duplicateCount;
    private long _driftedCount;
    private long _retentionRiskCount;
    private long _publishFailureCount;
    private long _publishedCount;
    private long _writeRetryCount;

    /// <summary>Số dòng đã commit trong suốt vòng đời process này.</summary>
    public long InsertedCount => Interlocked.Read(ref _insertedCount);

    /// <summary>Các delivery được giải quyết về một source id đã tồn tại sẵn.</summary>
    public long DuplicateCount => Interlocked.Read(ref _duplicateCount);

    /// <summary>Số dòng đã lưu mà device clock của nó không thể tin cậy được.</summary>
    /// <remarks>
    /// M3 tính production shift từ <c>device_timestamp</c>, nên một run mà con số này giữ ở mức 0 thì
    /// hoặc là một nhà máy có đồng hồ hoàn hảo, hoặc là một fault chưa bao giờ được bật lên. Khả năng
    /// thứ hai cao hơn nhiều, và đây chính là con số phân biệt hai trường hợp đó trước khi M3 xây
    /// dựng trên dữ liệu.
    /// </remarks>
    public long DriftedCount => Interlocked.Read(ref _driftedCount);

    /// <summary>Số dòng đã lưu lệch hơn một chunk so với thời điểm chúng được ghi.</summary>
    /// <remarks>
    /// <para>
    /// Con số mà ADR-011 đã hứa, và cũng là lý do migration 007 đưa raw retention ra khỏi lịch. Hypertable
    /// được partition theo <c>device_timestamp</c>, một ngày một chunk, nên một reading có device clock
    /// cách <c>recorded_at</c> một ngày trở lên sẽ bị xếp vào một chunk chẳng liên quan gì tới lúc nhà
    /// máy thực sự sản xuất ra nó. Đủ xa và chunk đó đã vượt qua horizon 400 ngày: dòng đó vẫn được
    /// lưu, đúng, và bị lần retention kế tiếp xóa đi mà không có lỗi nào cả.
    /// </para>
    /// <para>
    /// Cố ý KHÔNG phải cùng câu hỏi với <see cref="DriftedCount"/>, thứ so sánh device clock với
    /// clock của gateway ở ngưỡng năm phút và trả lời "timestamp này có đáng tin không". Cái này so
    /// sánh device clock với thời điểm dòng được ghi, ở độ rộng của một chunk, và trả lời "dòng này
    /// có thể đã rơi vào nơi mà retention sẽ chạm tới hay không". Một lần flush buffer hai giờ thì
    /// drifted-clean và không liên quan ở đây; một cycler có đồng hồ báo năm 2024 mới là trường hợp
    /// cái này tồn tại để bắt.
    /// </para>
    /// <para>
    /// Báo cáo theo từng site (K3), vì câu trả lời cho "có nên bật lại retention không" là một câu
    /// trả lời theo từng nhà máy: một dòng với một cycler hỏng không được phép bị chín cycler tốt
    /// khác trung bình hóa cho biến mất.
    /// </para>
    /// </remarks>
    public long RetentionRiskCount => Interlocked.Read(ref _retentionRiskCount);

    /// <summary>Publish attempt thất bại. Với outbox, intent vẫn còn và sẽ được retry.</summary>
    /// <remarks>
    /// Trước M6, một failure trên đường publish trực tiếp có thể mất event (ADR-022). Với outbox,
    /// counter này đếm attempt thất bại; event vẫn pending và sẽ được retry. Nó không phải số event
    /// mất hoặc độ sâu outbox.
    /// </remarks>
    public long PublishFailureCount => Interlocked.Read(ref _publishFailureCount);

    /// <summary>Event đã giao cho broker mà không lỗi.</summary>
    /// <remarks>
    /// Nửa phía publisher của chaos lab. Không có nó, "bao nhiêu cái đã mất" chỉ có thể trả lời bằng
    /// cách đếm dòng và hy vọng mỗi dòng đều tạo ra một event — trong khi whitelist của scope.md §5.5
    /// nghĩa là phần lớn dòng cố ý không làm vậy. Một lab phải giả định chính số lượng input của nó
    /// thì đo được gì khi giả định đó sai.
    /// </remarks>
    public long PublishedCount => Interlocked.Read(ref _publishedCount);

    /// <summary>Write transaction bị PostgreSQL giết để phá một deadlock, và process này đã retry.</summary>
    /// <remarks>
    /// <para>
    /// Giá trị kỳ vọng là 0, kể cả ở ranh giới chunk. Writer giờ pre-create mọi slice hàng ngày còn
    /// thiếu trong autocommit trước khi bất kỳ transaction nào claim một message, việc này loại bỏ
    /// lock cycle giữa chunk-DDL và claim-table. Retry có giới hạn vẫn còn đó như một lưới an toàn
    /// cho các deadlock <c>40P01</c> không liên quan và các serialization failure <c>40001</c>.
    /// </para>
    /// <para>
    /// Nó được đếm thay vì bị nuốt vì bất kỳ giá trị khác 0 nào giờ đây là một tín hiệu cần điều tra,
    /// không phải chunk creation bình thường. N-M3-10 đo được 119 lần retry khi lời gọi pre-create bị
    /// bỏ đi và bằng 0 khi nó được khôi phục lại, trên cùng một fixture chạy đồng thời 16 ngày.
    /// </para>
    /// </remarks>
    public long WriteRetryCount => Interlocked.Read(ref _writeRetryCount);

    internal void RecordRetentionRisk(string siteId, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _retentionRiskCount, count);
        RetentionRiskCounter.Add(count, new KeyValuePair<string, object?>("site_id", siteId));
    }

    internal void RecordWriteRetry()
    {
        Interlocked.Increment(ref _writeRetryCount);
        WriteRetryCounter.Add(1);
    }

    internal void RecordPublishOutcome(int attempted, int failures)
    {
        var published = attempted - failures;

        if (published > 0)
        {
            Interlocked.Add(ref _publishedCount, published);
            PublishedCounter.Add(published);
        }

        if (failures <= 0)
        {
            return;
        }

        Interlocked.Add(ref _publishFailureCount, failures);
        PublishFailureCounter.Add(failures);
    }

    internal void RecordCommitted(IngestionResult result)
    {
        Interlocked.Add(ref _insertedCount, result.Inserted);
        Interlocked.Add(ref _duplicateCount, result.Duplicates);
        Interlocked.Add(ref _driftedCount, result.Drifted);
        InsertedCounter.Add(result.Inserted);
        DuplicateCounter.Add(result.Duplicates);
        DriftedCounter.Add(result.Drifted);
    }
}

/// <summary>Kết quả của một ingestion transaction đã commit.</summary>
/// <param name="Inserted">Reading logic mới đã lưu.</param>
/// <param name="Duplicates">Các delivery lặp lại bị loại bỏ.</param>
/// <param name="Drifted">Reading đã lưu mà device clock của nó không khớp với clock của gateway.</param>
/// <param name="PublishFailures">
/// Chỉ có nghĩa với đường publish trực tiếp cũ. Khi dùng outbox, kết quả ingestion phản ánh
/// việc commit intent, còn dispatcher ghi outcome của publish sau đó.
/// </param>
/// <param name="RetentionRisk">
/// Reading đã lưu mà device clock của nó lệch hơn một chunk so với <c>recorded_at</c>, và vì vậy rơi
/// vào một chunk mà retention sẽ đánh giá theo sai ngày. Xem
/// <see cref="IngestionMetrics.RetentionRiskCount"/>.
/// </param>
public sealed record IngestionResult(
    int Inserted,
    int Duplicates,
    int Drifted = 0,
    int PublishFailures = 0,
    int RetentionRisk = 0);
