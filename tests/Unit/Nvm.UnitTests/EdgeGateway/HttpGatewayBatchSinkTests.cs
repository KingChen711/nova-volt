using System.Net;
using Nvm.EdgeGateway;
using Nvm.EdgeGateway.Forwarding;
using Nvm.Sparkplug;

namespace Nvm.UnitTests.EdgeGateway;

public sealed class HttpGatewayBatchSinkTests
{
    [Fact]
    public async Task SendAsync_PostsVersionedProtobufBodyThatIngestionCanDecode()
    {
        var handler = new CapturingHandler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        var options = new EdgeGatewayOptions();
        var sink = new HttpGatewayBatchSink(client, options);
        var message = SparkplugIngressBatchCodecTests.Message(
            new DeviceReading(
                "Formation/Voltage",
                1,
                new MetricValue.Real(3.7),
                new DateTimeOffset(2026, 8, 28, 9, 15, 30, TimeSpan.Zero)));

        await sink.SendAsync([message], TestContext.Current.CancellationToken);

        handler.Method.ShouldBe(HttpMethod.Post);
        handler.RequestUri.ShouldBe(options.IngestionEndpoint);
        handler.ContentType.ShouldBe(HttpGatewayBatchSink.ProtobufMediaType);
        SparkplugIngressBatchCodec.Decode(handler.Body).ShouldHaveSingleItem().ShouldBe(message);
    }

    [Fact]
    public async Task SendAsync_NonSuccessStatus_IsNotAcknowledgedAsForwarded()
    {
        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var sink = new HttpGatewayBatchSink(client, new EdgeGatewayOptions());
        var message = SparkplugIngressBatchCodecTests.Message();

        await Should.ThrowAsync<HttpRequestException>(() =>
            sink.SendAsync([message], TestContext.Current.CancellationToken));
    }

    private sealed class CapturingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        internal HttpMethod? Method { get; private set; }

        internal Uri? RequestUri { get; private set; }

        internal string? ContentType { get; private set; }

        internal byte[] Body { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            return new HttpResponseMessage(statusCode);
        }
    }
}
