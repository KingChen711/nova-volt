using System.Net;
using System.Net.Http.Headers;
using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Forwarding;

/// <summary>POST các batch protobuf tới ingestion bên trong <c>dmz-net</c>.</summary>
public sealed class HttpGatewayBatchSink : IGatewayBatchSink
{
    /// <summary>Media type của body theo ADR-027.</summary>
    public const string ProtobufMediaType = "application/x-protobuf";

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _clock;
    private readonly Uri _endpoint;

    /// <summary>Tạo sender biến một câu trả lời overload thành một chỉ dẫn pacing.</summary>
    /// <param name="httpClient">Client đã mang sẵn request timeout của gateway.</param>
    /// <param name="options">Cấu hình gateway chứa ingestion endpoint.</param>
    /// <param name="clock">Phân giải một <c>Retry-After</c> được biểu diễn dưới dạng HTTP date.</param>
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

    /// <summary>Chuyển đổi một header <c>Retry-After</c> thành một delay mà tiến trình này có thể chờ.</summary>
    /// <param name="header">Giá trị header đã parse, hoặc null khi response không mang header nào.</param>
    /// <param name="now">Thời điểm tham chiếu cho một header được biểu diễn dưới dạng HTTP date.</param>
    /// <returns>Delay được yêu cầu, hoặc null khi header bị thiếu hoặc đã ở trong quá khứ.</returns>
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

    // 429 và 503 là hai câu trả lời mang nghĩa "sau này, chậm hơn" chứ không phải "không bao giờ".
    // Một 500 là bug ở ingestion và một 400 là bug ở phía ta; không cái nào là lý do để đổi nhịp độ.
    private static bool IsBackpressure(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
}
