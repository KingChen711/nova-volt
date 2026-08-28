using System.Net;
using System.Net.Http.Headers;
using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Forwarding;

/// <summary>POSTs protobuf batches to ingestion inside <c>dmz-net</c>.</summary>
public sealed class HttpGatewayBatchSink : IGatewayBatchSink
{
    /// <summary>The media type of ADR-027's body.</summary>
    public const string ProtobufMediaType = "application/x-protobuf";

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _clock;
    private readonly Uri _endpoint;

    /// <summary>Creates the sender that turns an overload answer into a pacing instruction.</summary>
    /// <param name="httpClient">Client already carrying the gateway request timeout.</param>
    /// <param name="options">Gateway configuration holding the ingestion endpoint.</param>
    /// <param name="clock">Resolves a <c>Retry-After</c> expressed as an HTTP date.</param>
    public HttpGatewayBatchSink(HttpClient httpClient, EdgeGatewayOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _httpClient = httpClient;
        _clock = clock;
        _endpoint = options.IngestionEndpoint;
    }

    /// <inheritdoc />
    public async Task SendAsync(
        IReadOnlyCollection<DecodedSparkplugMessage> messages,
        CancellationToken cancellationToken)
    {
        var body = SparkplugIngressBatchCodec.Encode(messages);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ProtobufMediaType);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (IsBackpressure(response.StatusCode))
        {
            throw new GatewayBackpressureException(
                response.StatusCode,
                ReadRetryAfter(response.Headers.RetryAfter, _clock.GetUtcNow()));
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Converts a <c>Retry-After</c> header into a delay this process can wait for.</summary>
    /// <param name="header">Parsed header value, or null when the response carried none.</param>
    /// <param name="now">Reference instant for a header expressed as an HTTP date.</param>
    /// <returns>The requested delay, or null when the header is missing or already in the past.</returns>
    public static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : null;
        }

        if (header.Date is { } date)
        {
            var remaining = date - now;
            return remaining > TimeSpan.Zero ? remaining : null;
        }

        return null;
    }

    // 429 and 503 are the two answers that mean "later, slower" rather than "never". A 500 is a bug
    // in ingestion and a 400 is a bug in us; neither is a reason to change pace.
    private static bool IsBackpressure(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
}
