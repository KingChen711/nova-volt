using Nvm.Contracts.Events.Quality;

namespace Nvm.Ingestion.Publishing;

/// <summary>Lưu telemetry và không announce gì cả.</summary>
/// <remarks>
/// Cái mà một migration job, một unit test, hoặc một deployment không whitelist signal nào chạy
/// cùng. Nó tồn tại để "không cấu hình bus" là một cấu hình, không phải một null check lặp lại ở mỗi
/// call site — và để một publisher bị thiếu không bao giờ bị nhầm với một publish đã thất bại.
/// </remarks>
public sealed class NullMeasurementEventPublisher : IMeasurementEventPublisher
{
    /// <summary>Instance dùng chung.</summary>
    public static NullMeasurementEventPublisher Instance { get; } = new();

    /// <inheritdoc />
    public Task<int> PublishAsync(
        IReadOnlyCollection<MeasurementRecorded> events,
        CancellationToken cancellationToken) => Task.FromResult(0);
}
