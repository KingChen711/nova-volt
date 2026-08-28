using Nvm.Ingestion.Persistence;
using Nvm.Sparkplug;

namespace Nvm.Ingestion;

internal static class IngestionEndpoints
{
    internal const string SparkplugBatchPath = "/api/ingestion/v1/sparkplug-batches";
    internal const string ProtobufMediaType = "application/x-protobuf";

    internal static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(SparkplugBatchPath, IngestSparkplugBatchAsync)
            .RequireRateLimiting(IngestionAdmissionControl.PolicyName);
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
