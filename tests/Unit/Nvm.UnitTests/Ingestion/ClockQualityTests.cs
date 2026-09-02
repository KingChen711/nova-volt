using Nvm.Ingestion;

namespace Nvm.UnitTests.Ingestion;

public sealed class ClockQualityTests
{
    private static readonly DateTimeOffset Gateway = new(2026, 8, 28, 9, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = ClockQualityClassifier.DefaultThreshold;

    [Fact]
    public void TenSecondsApart_IsGood()
    {
        ClockQualityClassifier
            .Classify(Gateway.AddSeconds(-10), Gateway, Threshold)
            .ShouldBe(ClockQuality.Good);
    }

    [Fact]
    public void TwoHoursBehind_IsDrifted()
    {
        // D5. Reading vẫn được classify thay vì bị từ chối; có lưu nó hay không là câu hỏi của
        // IngestionDriftedReadingTests, và câu trả lời ở đó là có.
        ClockQualityClassifier
            .Classify(Gateway.AddHours(-2), Gateway, Threshold)
            .ShouldBe(ClockQuality.Drifted);
    }

    [Fact]
    public void TwoHoursAhead_IsAlsoDrifted()
    {
        // Board thay sáng nay khởi động với factory default và gắn timestamp vào tương lai. Chỉ compare
        // theo một chiều sẽ gọi nó là Good.
        ClockQualityClassifier
            .Classify(Gateway.AddHours(2), Gateway, Threshold)
            .ShouldBe(ClockQuality.Drifted);
    }

    [Fact]
    public void ExactlyOnTheThreshold_IsStillGood()
    {
        // Năm phút là tolerance, không phải failure đầu tiên. Compare nghiêm ngặt ở đây sẽ làm boundary
        // phụ thuộc vào nhiễu millisecond mà không ai cấu hình.
        ClockQualityClassifier
            .Classify(Gateway - Threshold, Gateway, Threshold)
            .ShouldBe(ClockQuality.Good);

        ClockQualityClassifier
            .Classify(Gateway - Threshold - TimeSpan.FromMilliseconds(1), Gateway, Threshold)
            .ShouldBe(ClockQuality.Drifted);
    }

    [Fact]
    public void NoDeviceClockAtAll_IsUnknownRatherThanAThrow()
    {
        // CSV file drop của C15 là nguồn thực: thiết bị test end-of-line export row và chưa từng có
        // device clock tham gia. Unknown nói đúng điều đó; Good sẽ là khẳng định không ai đưa ra.
        ClockQualityClassifier
            .Classify(deviceTimestamp: null, Gateway, Threshold)
            .ShouldBe(ClockQuality.Unknown);
    }

    [Fact]
    public void ALongBufferedDelay_DoesNotMakeAGoodClockDrifted()
    {
        // Store-and-forward có thể giữ reading nhiều giờ (ADR-028). Threshold đặt ở việc hai clock
        // bất đồng, không phải reading đến trễ — nếu không mọi message sống qua outage sẽ đến với flag,
        // và flag sẽ không còn mang nghĩa gì.
        var device = Gateway.AddSeconds(-3);

        ClockQualityClassifier.Classify(device, Gateway, Threshold).ShouldBe(ClockQuality.Good);
    }

    [Fact]
    public void NegativeThreshold_IsRefused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            ClockQualityClassifier.Classify(Gateway, Gateway, TimeSpan.FromMinutes(-1)));
    }

    [Theory]
    [InlineData(ClockQuality.Good, "Good")]
    [InlineData(ClockQuality.Drifted, "Drifted")]
    [InlineData(ClockQuality.Unknown, "Unknown")]
    public void ColumnValues_MatchTheCheckConstraint(ClockQuality quality, string expected)
    {
        // Database CHECK liệt kê ba cách viết này. Rename enum member là refactor mà không ai kỳ vọng
        // sẽ làm hỏng INSERT, nên mapping được pin ở đây thay vì suy luận.
        quality.ToColumnValue().ShouldBe(expected);
    }
}
