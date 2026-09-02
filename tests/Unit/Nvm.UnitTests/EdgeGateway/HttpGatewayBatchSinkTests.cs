using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using Nvm.EdgeGateway;
using Nvm.EdgeGateway.Forwarding;
using Nvm.Sparkplug;

namespace Nvm.UnitTests.EdgeGateway;

public sealed class HttpGatewayBatchSinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 9, 15, 30, TimeSpan.Zero);

    [Fact]
    public async Task SendAsync_PostsVersionedProtobufBodyThatIngestionCanDecode()
    {
        var handler = new CapturingHandler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        var options = new EdgeGatewayOptions();
        var sink = new HttpGatewayBatchSink(client, options, new FakeTimeProvider(Now));
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
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler);
        var sink = new HttpGatewayBatchSink(client, new EdgeGatewayOptions(), new FakeTimeProvider(Now));
        var message = SparkplugIngressBatchCodecTests.Message();

        await Should.ThrowAsync<HttpRequestException>(() =>
            sink.SendAsync([message], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task SendAsync_OverloadStatus_CarriesTheServerRequestedDelay(HttpStatusCode statusCode)
    {
        var handler = new CapturingHandler(statusCode, new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)));
        using var client = new HttpClient(handler);
        var sink = new HttpGatewayBatchSink(client, new EdgeGatewayOptions(), new FakeTimeProvider(Now));

        var exception = await Should.ThrowAsync<GatewayBackpressureException>(() =>
            sink.SendAsync([SparkplugIngressBatchCodecTests.Message()], TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(statusCode);
        exception.RetryAfter.ShouldBe(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task SendAsync_OverloadWithoutRetryAfter_LeavesThePaceToTheGateway()
    {
        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        var sink = new HttpGatewayBatchSink(client, new EdgeGatewayOptions(), new FakeTimeProvider(Now));

        var exception = await Should.ThrowAsync<GatewayBackpressureException>(() =>
            sink.SendAsync([SparkplugIngressBatchCodecTests.Message()], TestContext.Current.CancellationToken));

        exception.RetryAfter.ShouldBeNull();
    }

    [Fact]
    public void ReadRetryAfter_HttpDate_IsResolvedAgainstTheInjectedClock()
    {
        var header = new RetryConditionHeaderValue(Now.AddSeconds(30));

        HttpGatewayBatchSink.ReadRetryAfter(header, Now).ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ReadRetryAfter_DateAlreadyPassed_IsIgnoredRatherThanNegative()
    {
        // Một response bị lệch đồng hồ hoặc bị xếp hàng chờ không được phép trao cho flusher một
        // khoảng chờ âm: Task.Delay sẽ ném exception và biến "chậm lại" thành một vòng lặp crash.
        var header = new RetryConditionHeaderValue(Now.AddSeconds(-30));

        HttpGatewayBatchSink.ReadRetryAfter(header, Now).ShouldBeNull();
    }

    private sealed class CapturingHandler(HttpStatusCode statusCode, RetryConditionHeaderValue? retryAfter = null)
        : HttpMessageHandler
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

            var response = new HttpResponseMessage(statusCode);
            response.Headers.RetryAfter = retryAfter;
            return response;
        }
    }
}
