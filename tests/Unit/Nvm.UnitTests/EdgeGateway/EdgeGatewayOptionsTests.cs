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

        // The ceiling is batch size times fsync rate. Measured on 2026-08-29: the fsync batch ran
        // saturated at 127,4 of 128 records while the disk sustained ~40 fsync/s, which is 5.120
        // msg/s - just under N1, and the gateway then backpressured the publisher through EMQX down
        // to 4.981 msg/s. Widening the batch buys headroom without acknowledging before fsync; the
        // latency an individual publish waits is still bounded by FsyncInterval.
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
