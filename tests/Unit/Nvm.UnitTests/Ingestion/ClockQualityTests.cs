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
        // D5. The reading is still classified rather than refused; whether it is stored is
        // IngestionDriftedReadingTests' question, and the answer there is yes.
        ClockQualityClassifier
            .Classify(Gateway.AddHours(-2), Gateway, Threshold)
            .ShouldBe(ClockQuality.Drifted);
    }

    [Fact]
    public void TwoHoursAhead_IsAlsoDrifted()
    {
        // A board replaced this morning comes up on its factory default and stamps into the future.
        // Comparing in one direction only would call that Good.
        ClockQualityClassifier
            .Classify(Gateway.AddHours(2), Gateway, Threshold)
            .ShouldBe(ClockQuality.Drifted);
    }

    [Fact]
    public void ExactlyOnTheThreshold_IsStillGood()
    {
        // Five minutes is the tolerance, not the first failure. A strict comparison here would make
        // the boundary depend on millisecond noise nobody configured.
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
        // The CSV file drop of C15 is the real source: an end-of-line tester exports rows and no
        // device clock was ever involved. Unknown says that; Good would be a claim nobody made.
        ClockQualityClassifier
            .Classify(deviceTimestamp: null, Gateway, Threshold)
            .ShouldBe(ClockQuality.Unknown);
    }

    [Fact]
    public void ALongBufferedDelay_DoesNotMakeAGoodClockDrifted()
    {
        // Store-and-forward can hold a reading for hours (ADR-028). The threshold is on the two
        // clocks disagreeing, not on the reading being late — otherwise every message that survived
        // an outage would arrive flagged, and the flag would stop meaning anything.
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
        // The database CHECK lists these three spellings. Renaming an enum member is a refactor
        // nobody expects to break an INSERT, so the mapping is pinned here rather than inferred.
        quality.ToColumnValue().ShouldBe(expected);
    }
}
