using Nvm.Contracts.Events.Quality;

namespace Nvm.Ingestion.Publishing;

/// <summary>Mang các measurement đã commit đi tiếp dưới dạng domain event.</summary>
/// <remarks>
/// Được gọi <b>sau</b> khi database transaction commit, không bao giờ ở bên trong nó. Ingestion và
/// RabbitMQ là hai hệ thống không chia sẻ transaction, và giả vờ ngược lại chính là điều ADR-022 đã
/// đo được: 18 trên 200 event bị mất khi broker chết giữa lúc publish. M2 giữ nguyên giới hạn đó và
/// đếm nó thay vì che giấu nó; transactional outbox đóng lỗ hổng này là M6.
/// </remarks>
public interface IMeasurementEventPublisher
{
    /// <summary>Publish các event cho một batch đã commit.</summary>
    /// <param name="events">Event cho những reading thực sự đã được lưu.</param>
    /// <param name="cancellationToken">Dừng việc publish khi host tắt.</param>
    /// <returns>Bao nhiêu cái publish thất bại. Các dòng telemetry vẫn ở nguyên đó dù thế nào.</returns>
    Task<int> PublishAsync(
        IReadOnlyCollection<MeasurementRecorded> events,
        CancellationToken cancellationToken);
}
