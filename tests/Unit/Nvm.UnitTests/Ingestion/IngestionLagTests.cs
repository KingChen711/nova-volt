using Nvm.Ingestion;

namespace Nvm.UnitTests.Ingestion;

public sealed class IngestionLagTests
{
    [Fact]
    public void NoSamples_ReportsZerosRatherThanThrowing()
    {
        // The stats endpoint is polled during a run that may not have stored anything yet, and a
        // load lab that fails on an empty window fails for the wrong reason.
        new IngestionLag().Snapshot().ShouldBe(new IngestionLagSnapshot(0, 0, 0, 0, 0));
    }

    [Fact]
    public void Percentiles_AreNearestRankOverWhatWasActuallyMeasured()
    {
        var lag = new IngestionLag();

        for (var milliseconds = 1; milliseconds <= 100; milliseconds++)
        {
            lag.Record(TimeSpan.FromMilliseconds(milliseconds));
        }

        var snapshot = lag.Snapshot();

        snapshot.Samples.ShouldBe(100);
        snapshot.P50.ShouldBe(0.050, tolerance: 0.0005);
        snapshot.P95.ShouldBe(0.095, tolerance: 0.0005);
        snapshot.P99.ShouldBe(0.099, tolerance: 0.0005);
        snapshot.Max.ShouldBe(0.100, tolerance: 0.0005);
    }

    [Fact]
    public void TheWindowIsRecent_SoAPipelineThatFellBehindCannotHideBehindEarlierGoodBehaviour()
    {
        // D2 asks whether the rate is SUSTAINED. A cumulative percentile lets ten good minutes bury
        // the two bad ones at the end, which is precisely the failure the criterion is about.
        var lag = new IngestionLag();

        for (var index = 0; index < IngestionLag.WindowSize; index++)
        {
            lag.Record(TimeSpan.FromMilliseconds(1));
        }

        for (var index = 0; index < IngestionLag.WindowSize; index++)
        {
            lag.Record(TimeSpan.FromSeconds(9));
        }

        var snapshot = lag.Snapshot();

        snapshot.Samples.ShouldBe(IngestionLag.WindowSize * 2);
        snapshot.P50.ShouldBe(9);
        snapshot.P95.ShouldBe(9);
    }

    [Fact]
    public void ASingleSample_IsEveryPercentile()
    {
        var lag = new IngestionLag();
        lag.Record(TimeSpan.FromSeconds(2.5));

        var snapshot = lag.Snapshot();

        snapshot.P50.ShouldBe(2.5);
        snapshot.P95.ShouldBe(2.5);
        snapshot.P99.ShouldBe(2.5);
        snapshot.Max.ShouldBe(2.5);
    }
}
