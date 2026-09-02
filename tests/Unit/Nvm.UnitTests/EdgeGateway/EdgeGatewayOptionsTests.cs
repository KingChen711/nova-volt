using Nvm.EdgeGateway;

namespace Nvm.UnitTests.EdgeGateway;

public sealed class EdgeGatewayOptionsTests
{
    [Fact]
    public void MqttFiveSession_IsRetainedUntilUnacknowledgedDeliveriesCanReconnect()
    {
        var options = new EdgeGatewayOptions
        {
            SessionExpiryInterval = TimeSpan.FromHours(2),
        };

        var mqtt = EdgeGatewayWorker.BuildMqttOptions(options);

        mqtt.CleanSession.ShouldBeFalse();
        mqtt.SessionExpiryInterval.ShouldBe(7_200u);
    }

    [Fact]
    public void FlushBatch_IsLargerThanFsyncBatchWithoutExceedingIngestionLimits()
    {
        var buffer = new EdgeGatewayOptions().Buffer;

        // Trần này bằng batch size nhân với tốc độ fsync. Đo được vào ngày 2026-08-29: fsync batch
        // chạy bão hòa ở mức 127,4 trên 128 record trong khi ổ đĩa duy trì được ~40 fsync/s, tức là
        // 5.120 msg/s - vừa dưới N1, và gateway sau đó backpressure ngược publisher qua EMQX xuống
        // còn 4.981 msg/s. Nới rộng batch mua thêm dư địa mà không cần acknowledge trước khi fsync;
        // độ trễ mà một lần publish đơn lẻ phải chờ vẫn bị giới hạn bởi FsyncInterval.
        buffer.FsyncBatchSize.ShouldBe(128);
        buffer.FlushBatchSize.ShouldBe(1_024);
        buffer.FlushBatchBytes.ShouldBe(1_024 * 1_024);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveSessionExpiry_IsRefused(double seconds)
    {
        var options = new EdgeGatewayOptions
        {
            SessionExpiryInterval = TimeSpan.FromSeconds(seconds),
        };

        Should.Throw<InvalidOperationException>(options.Validate);
    }
}
