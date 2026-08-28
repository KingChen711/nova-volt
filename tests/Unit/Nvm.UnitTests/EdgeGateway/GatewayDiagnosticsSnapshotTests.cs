using Nvm.EdgeGateway;

namespace Nvm.UnitTests.EdgeGateway;

public sealed class GatewayDiagnosticsSnapshotTests
{
    [Fact]
    public void TextContainsEveryExactCounterAsAStableKey()
    {
        var capturedAt = new DateTimeOffset(2026, 8, 29, 4, 30, 0, TimeSpan.Zero);
        var snapshot = new GatewayDiagnosticsSnapshot(
            capturedAt,
            Decoded: 101,
            Buffered: 100,
            Forwarded: 99,
            Rejected: 2,
            RebirthRequests: 3,
            BufferFullEvents: 4,
            ThrottledFlushes: 5,
            RateLimitedFlushes: 6,
            BufferDepth: 1,
            BufferBytes: 8192,
            CorruptRecords: 7,
            TruncatedTails: 8,
            DataFsyncs: 9,
            FlushBatches: 10);

        snapshot.ToText().Split('\n', StringSplitOptions.RemoveEmptyEntries).ShouldBe(
        [
            "captured_at=2026-08-29T04:30:00.0000000+00:00",
            "decoded=101",
            "buffered=100",
            "forwarded=99",
            "rejected=2",
            "rebirth_requests=3",
            "buffer_full_events=4",
            "throttled_flushes=5",
            "rate_limited_flushes=6",
            "buffer_depth=1",
            "buffer_bytes=8192",
            "corrupt_records=7",
            "truncated_tails=8",
            "data_fsyncs=9",
            "flush_batches=10",
        ]);
    }
}
