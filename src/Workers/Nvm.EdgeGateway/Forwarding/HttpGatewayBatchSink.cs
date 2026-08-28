using System.Net.Http.Headers;
using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Forwarding;

/// <summary>POSTs protobuf batches to ingestion inside <c>dmz-net</c>.</summary>
public sealed class HttpGatewayBatchSink : IGatewayBatchSink
{
    /// <summary>The media type of ADR-027's body.</summary>
    public const string ProtobufMediaType = "application/x-protobuf";

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;

    /// <summary>Creates the direct C08 sender. C09 puts a durable buffer in front of it.</summary>
    public HttpGatewayBatchSink(HttpClient httpClient, EdgeGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClient;
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

        response.EnsureSuccessStatusCode();
    }
}
