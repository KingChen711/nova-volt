using Nvm.Ingestion.FileDrop;
using Nvm.Sparkplug;

namespace Nvm.Ingestion.Persistence;

/// <summary>Con đường dedup duy nhất, dùng chung bởi mọi device adapter.</summary>
/// <remarks>
/// Hai điểm vào, một cánh cửa. Các adapter chỉ khác nhau ở cách chúng <b>đọc</b> — protobuf từ MQTT,
/// text từ một share — và không khác gì khác: cả hai đều xây natural key bằng cùng một lời gọi và
/// đều commit qua cùng một transaction. Một định nghĩa dedup thứ hai sẽ trôi dạt khỏi cái đầu tiên
/// chỉ sau vài tháng, và triệu chứng sẽ là một measurement bị lưu hai lần đúng cho những máy báo cáo
/// qua cả hai route (C15.1).
/// </remarks>
public interface IMeasurementIngestor
{
    /// <summary>Claim các natural key và lưu reading của chúng trong một database transaction.</summary>
    /// <param name="messages">Các Sparkplug message đã decode từ edge gateway.</param>
    /// <param name="cancellationToken">Dừng công việc khi host tắt.</param>
    Task<IngestionResult> IngestAsync(
        IReadOnlyCollection<DecodedSparkplugMessage> messages,
        CancellationToken cancellationToken);

    /// <summary>Lưu các measurement đọc từ một file được drop vào, qua cùng bước claim và insert.</summary>
    /// <param name="measurements">Các dòng đã parse được.</param>
    /// <param name="readAt">
    /// Lúc ingestion đọc file. Lưu thành <c>gateway_timestamp</c> — đây là đồng hồ đáng tin cậy đầu
    /// tiên mà reading đi qua, đúng như ý nghĩa của cột đó.
    /// </param>
    /// <param name="cancellationToken">Dừng công việc khi host tắt.</param>
    Task<IngestionResult> IngestAsync(
        IReadOnlyCollection<FileMeasurement> measurements,
        DateTimeOffset readAt,
        CancellationToken cancellationToken);
}
