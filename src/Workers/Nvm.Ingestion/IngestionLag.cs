using System.Diagnostics.Metrics;

namespace Nvm.Ingestion;

/// <summary>How long a reading took to get from the device clock into the database.</summary>
/// <remarks>
/// <para>
/// <c>recorded_at − device_timestamp</c>, which is D2's number. It spans the whole path — the
/// device's own buffering, MQTT, the gateway's fsync, store-and-forward, HTTP, the transaction — so
/// it is the only lag figure that answers the question an operator actually asks: how old is the
/// newest thing I can see.
/// </para>
/// <para>
/// <b>Only readings with a good clock are sampled.</b> The measurement subtracts two clocks, so a
/// PLC two hours out contributes a two-hour lag that says nothing about the pipeline, and a PLC two
/// hours fast contributes a negative one. Either would ruin a percentile. C16's harness therefore
/// runs with clock drift switched off, and that fact belongs next to every number this produces —
/// otherwise somebody at M8 reads the table and concludes lag was once negative.
/// </para>
/// </remarks>
public sealed class IngestionLag
{
    /// <summary>How many recent samples the percentiles are computed over.</summary>
    /// <remarks>
    /// A ring, not the whole run. D2 asks whether the system <i>sustains</i> a rate, and a cumulative
    /// percentile hides a pipeline that has been falling behind for the last two minutes under ten
    /// minutes of earlier good behaviour.
    /// </remarks>
    public const int WindowSize = 8192;

    private static readonly Meter Meter = new("Nvm.Ingestion", "1.0.0");
    private static readonly Histogram<double> LagHistogram =
        Meter.CreateHistogram<double>("nvm.ingest.lag", unit: "s");

    private readonly double[] _samples = new double[WindowSize];
    private readonly Lock _gate = new();
    private long _total;
    private int _next;
    private int _filled;

    /// <summary>Records one reading's lag.</summary>
    /// <param name="lag">Time between the device clock and the commit.</param>
    public void Record(TimeSpan lag)
    {
        LagHistogram.Record(lag.TotalSeconds);

        lock (_gate)
        {
            _samples[_next] = lag.TotalSeconds;
            _next = (_next + 1) % WindowSize;
            _total++;

            if (_filled < WindowSize)
            {
                _filled++;
            }
        }
    }

    /// <summary>The percentiles D2 is measured against.</summary>
    public IngestionLagSnapshot Snapshot()
    {
        double[] window;

        lock (_gate)
        {
            if (_filled == 0)
            {
                return new IngestionLagSnapshot(0, 0, 0, 0, 0);
            }

            window = _samples[.._filled];
            window = [.. window];
        }

        Array.Sort(window);

        return new IngestionLagSnapshot(
            _total,
            Percentile(window, 0.50),
            Percentile(window, 0.95),
            Percentile(window, 0.99),
            window[^1]);
    }

    // Nearest-rank on a sorted copy. Interpolating between two samples would invent a lag no reading
    // ever had, and D2's threshold is compared against a measurement, not against a model.
    private static double Percentile(double[] sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;

        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}

/// <summary>Lag percentiles over the recent window, in seconds.</summary>
/// <param name="Samples">Total readings sampled since the process started.</param>
/// <param name="P50">Median lag.</param>
/// <param name="P95">D2's number: must stay below five seconds at 5.000 msg/s.</param>
/// <param name="P99">Tail lag.</param>
/// <param name="Max">Worst lag in the window.</param>
public sealed record IngestionLagSnapshot(long Samples, double P50, double P95, double P99, double Max);
