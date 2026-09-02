using Nvm.Ingestion.Persistence;
using Nvm.Sparkplug;

namespace Nvm.Ingestion;

internal static class IngestionEndpoints
{
    internal const string SparkplugBatchPath = "/api/ingestion/v1/sparkplug-batches";
    internal const string StatsPath = "/api/ingestion/v1/stats";
    internal const string ProtobufMediaType = "application/x-protobuf";

    internal static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(SparkplugBatchPath, IngestSparkplugBatchAsync)
            .RequireRateLimiting(IngestionAdmissionControl.PolicyName);

        // Chỉ đọc, và cố ý nằm ngoài rate limiter: khoảnh khắc đáng để hỏi tới chính là lúc ingestion
        // bận rộn nhất, và một stats endpoint trả về 429 khi đang tải nặng thì đang báo cáo đúng cái
        // điều kiện mà nó không thể quan sát được.
        app.MapGet(StatsPath, ReadStats);
    }

    private static async Task<IResult> IngestSparkplugBatchAsync(
        HttpRequest request,
        IMeasurementIngestor ingestor,
        IngestionOptions options,
        CancellationToken cancellationToken)
    {
        if (!HasProtobufContentType(request.ContentType))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: $"Content-Type must be {ProtobufMediaType}.");
        }

        if (request.ContentLength > options.MaxRequestBytes)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: $"Batch exceeds the {options.MaxRequestBytes} byte limit.");
        }

        try
        {
            var body = await ReadBoundedAsync(request.Body, options.MaxRequestBytes, cancellationToken);
            var messages = SparkplugIngressBatchCodec.Decode(body);
            var readingCount = messages.Sum(message => (long)message.Readings.Length);

            if (readingCount > options.MaxReadingsPerBatch)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: $"Batch contains {readingCount} readings; limit is {options.MaxReadingsPerBatch}.");
            }

            var result = await ingestor.IngestAsync(messages, cancellationToken);
            return Results.Accepted(value: result);
        }
        catch (RequestBodyTooLargeException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: exception.Message);
        }
        catch (SparkplugIngressBatchException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static IResult ReadStats(IngestionMetrics metrics, IngestionLag lag)
    {
        var snapshot = lag.Snapshot();

        return Results.Ok(new IngestionStats(
            metrics.InsertedCount,
            metrics.DuplicateCount,
            metrics.DriftedCount,
            metrics.PublishedCount,
            metrics.PublishFailureCount,
            snapshot.Samples,
            snapshot.P50,
            snapshot.P95,
            snapshot.P99,
            snapshot.Max));
    }

    private static bool HasProtobufContentType(string? contentType) =>
        contentType is not null
        && string.Equals(
            contentType.Split(';', 2)[0].Trim(),
            ProtobufMediaType,
            StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 81_920;
        using var body = new MemoryStream();
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize);

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return body.ToArray();
            }

            if (body.Length + read > maxBytes)
            {
                throw new RequestBodyTooLargeException(maxBytes);
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private sealed class RequestBodyTooLargeException(int limit)
        : Exception($"Batch exceeds the {limit} byte limit.");
}

/// <summary>Những gì một ingestion process đã làm kể từ lúc khởi động, và nó đang chậm bao xa.</summary>
/// <param name="Inserted">Reading logic mới đã lưu.</param>
/// <param name="Duplicates">Các delivery lặp lại mà dedup key đã nuốt.</param>
/// <param name="Drifted">Reading đã lưu với một device clock không thể tin cậy được.</param>
/// <param name="Published">Event đã giao cho broker mà không lỗi.</param>
/// <param name="PublishFailures">Event có dòng đã lưu nhưng thông báo của nó bị mất.</param>
/// <param name="LagSamples">Reading được lấy mẫu cho lag — chỉ những đồng hồ tốt.</param>
/// <param name="LagP50Seconds">Lag trung vị từ device tới database.</param>
/// <param name="LagP95Seconds">Con số của D2.</param>
/// <param name="LagP99Seconds">Lag đuôi.</param>
/// <param name="LagMaxSeconds">Lag tệ nhất trong window gần đây.</param>
internal sealed record IngestionStats(
    long Inserted,
    long Duplicates,
    long Drifted,
    long Published,
    long PublishFailures,
    long LagSamples,
    double LagP50Seconds,
    double LagP95Seconds,
    double LagP99Seconds,
    double LagMaxSeconds);
