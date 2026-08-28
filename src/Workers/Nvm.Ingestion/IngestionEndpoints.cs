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

        // Read-only, and deliberately outside the rate limiter: the moment worth asking about is the
        // moment ingestion is busiest, and a stats endpoint that returns 429 under load reports on
        // exactly the conditions it cannot observe.
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

/// <summary>What one ingestion process has done since it started, and how far behind it is.</summary>
/// <param name="Inserted">New logical readings stored.</param>
/// <param name="Duplicates">Repeated deliveries the dedup key swallowed.</param>
/// <param name="Drifted">Readings stored with a device clock that could not be trusted.</param>
/// <param name="PublishFailures">Events whose row is stored and whose announcement was lost.</param>
/// <param name="LagSamples">Readings sampled for lag — good clocks only.</param>
/// <param name="LagP50Seconds">Median device-to-database lag.</param>
/// <param name="LagP95Seconds">D2's number.</param>
/// <param name="LagP99Seconds">Tail lag.</param>
/// <param name="LagMaxSeconds">Worst lag in the recent window.</param>
internal sealed record IngestionStats(
    long Inserted,
    long Duplicates,
    long Drifted,
    long PublishFailures,
    long LagSamples,
    double LagP50Seconds,
    double LagP95Seconds,
    double LagP99Seconds,
    double LagMaxSeconds);
