using Nvm.Simulator.Faults;

namespace Nvm.Simulator;

/// <summary>How fast the plant runs, and which part of it this process is pretending to be.</summary>
/// <remarks>
/// <para>
/// The two numbers that matter are <see cref="SamplePeriod"/> and <see cref="TimeCompression"/>, and
/// they do different jobs. The sample period is measured in <b>process time</b> and decides how many
/// measurements a cycle produces; the compression is measured against the <b>wall clock</b> and
/// decides how long the run takes. Changing the compression must not change a single measurement,
/// which is the property <c>SimulatorWorkerTests</c> exists to hold.
/// </para>
/// <para>
/// Compression is a multiplier on the clock, not a shorter sleep. A formation cycle is eighteen hours
/// and a test cannot wait eighteen hours, but it also cannot skip samples to get there — the count is
/// the left-hand side of D1's reconciliation.
/// </para>
/// </remarks>
public sealed class SimulatorOptions
{
    /// <summary>The line this process speaks for.</summary>
    public string LinePath { get; set; } = "NOVAVOLT/NV1/FORMATION/F1";

    /// <summary>Which model revision the channel list is read from. Null means the newest.</summary>
    /// <remarks>
    /// The plant is what says which channels exist. Inventing the list would produce topics ingestion
    /// refuses (K3), and a simulator whose every message is refused measures nothing.
    /// </remarks>
    public int? Revision { get; set; }

    /// <summary>Directory holding the <c>factory-model.r*.json</c> documents.</summary>
    public string SeedDirectory { get; set; } = "seed";

    /// <summary>How long one cell spends in a channel.</summary>
    public TimeSpan CycleDuration { get; set; } = TimeSpan.FromHours(18);

    /// <summary>How much process time passes between two samples of a channel.</summary>
    public TimeSpan SamplePeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How many seconds of plant time pass per second of wall clock.</summary>
    public double TimeCompression { get; set; } = 1000;

    /// <summary>MQTT broker host. On <c>ot-net</c> this is the EMQX service name.</summary>
    public string BrokerHost { get; set; } = "emqx";

    /// <summary>MQTT broker port.</summary>
    public int BrokerPort { get; set; } = 1883;

    /// <summary>The session number this run publishes under, in <c>bdSeq</c>.</summary>
    public ulong BirthDeathSequence { get; set; }

    /// <summary>How badly this plant is asked to misbehave. Everything off by default.</summary>
    public SimulatorFaults Faults { get; set; } = new();

    /// <summary>Where the run report is written — the left-hand side of D1's reconciliation.</summary>
    public string ReportPath { get; set; } = "reports/simulator-run.json";

    /// <summary>How often the run report is rewritten.</summary>
    /// <remarks>
    /// Measured on the wall clock, not on process time. The file exists so that a run can be
    /// reconciled while it is still going and after it has been killed, and both of those are
    /// questions about real minutes.
    /// </remarks>
    public TimeSpan ReportInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait before dialling the broker again after the connection drops.</summary>
    /// <remarks>
    /// A plant does not stop because the broker restarted (N15), so losing the connection has to be
    /// a pause rather than the end of a run. Two seconds matches the gateway's own reconnect delay:
    /// long enough that a broker restart is not met by a retry storm, short enough that the node is
    /// back before a consumer would call it stale.
    /// </remarks>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long the wall clock waits between two ticks.</summary>
    /// <exception cref="InvalidOperationException">The settings do not describe a runnable plant.</exception>
    public TimeSpan TickInterval => SamplePeriod / TimeCompression;

    /// <summary>Refuses settings that cannot produce a plant, before anything connects.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public void Validate()
    {
        Faults.Validate();

        if (TimeCompression <= 0)
        {
            throw new InvalidOperationException("Time compression must be positive.");
        }

        if (SamplePeriod <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The sample period must be positive.");
        }

        // A cycle that is not a whole number of samples long is legal but surprising: the last sample
        // of one cell and the first of the next are closer together than the rest. Refused so that
        // the reconciliation in D1 can multiply rather than explain.
        if (CycleDuration.Ticks % SamplePeriod.Ticks != 0)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"A cycle of {CycleDuration} is not a whole number of {SamplePeriod} samples."));
        }

        // Below the resolution of a system timer the wall clock stops keeping up, and the run silently
        // becomes slower than the compression claims — which would make a throughput number a lie.
        if (TickInterval < TimeSpan.FromMilliseconds(1))
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"A sample period of {SamplePeriod} compressed {TimeCompression}x leaves {TickInterval.TotalMilliseconds:0.###} ms per tick, which is below what a timer can hold. Lower the compression or lengthen the sample period."));
        }

        if (ReconnectDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Reconnect delay must be positive.");
        }

        if (ReportInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The run report interval must be positive; the reconciliation reads that file while the run is going.");
        }
    }
}
