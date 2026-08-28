namespace Nvm.Simulator.Faults;

/// <summary>The three ways this plant is allowed to misbehave, and how hard.</summary>
/// <remarks>
/// <para>
/// Every rate defaults to <b>zero</b>: a simulator nobody configured produces a well-behaved plant.
/// The labs turn them on, which is the arrangement M2 needs — a fault that is on by default gets
/// forgotten, and then every number the milestone produces has an unstated ingredient in it.
/// </para>
/// <para>
/// The opposite mistake is the one <c>R-M2-1</c> warns about: running the reconciliation with the
/// faults still off, watching it come out even, and marking D1 done having checked nothing. That is
/// why the run report writes these counts down next to the totals — a run with zero duplicates
/// blocked is visible rather than merely unremarkable.
/// </para>
/// </remarks>
public sealed class SimulatorFaults
{
    /// <summary>Share of published messages that are sent a second time. The lab uses <c>0.10</c>.</summary>
    /// <remarks>
    /// The <b>same</b> message, not another one like it. See
    /// <see cref="FaultInjectingPublisher"/> for why the difference decides whether D1 measures
    /// anything.
    /// </remarks>
    public double DuplicateRate { get; set; }

    /// <summary>Share of devices whose clock is wrong. The lab uses <c>0.10</c>.</summary>
    /// <remarks>
    /// Of <b>devices</b>, not of messages. A dead CMOS battery is wrong on every reading that channel
    /// ever takes, and a fault that drifted a random tenth of the messages would be a fault no
    /// hardware has — worse, it would be one the <c>Drifted</c> flag could not usefully group by.
    /// </remarks>
    public double DriftedDeviceRate { get; set; }

    /// <summary>How far a wrong clock is wrong. Applied as plus or minus, per device.</summary>
    public TimeSpan ClockDrift { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Average gap between two connection losses. Zero switches the fault off.</summary>
    /// <remarks>
    /// A mean rather than a period. Network blips arrive as a Poisson process, so the gaps are
    /// exponential; a fixed interval would let a run settle into a rhythm and let anything downstream
    /// accidentally depend on it.
    /// </remarks>
    public TimeSpan DropoutMeanInterval { get; set; }

    /// <summary>How long a connection loss lasts.</summary>
    public TimeSpan DropoutDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Seed for the fault dice, so a run can be repeated exactly.</summary>
    /// <remarks>
    /// A lab that cannot be re-run with the same faults is a lab whose surprising result cannot be
    /// investigated — only re-rolled until it goes away.
    /// </remarks>
    public int Seed { get; set; } = 20260828;

    /// <summary>Whether anything is switched on at all.</summary>
    public bool AnyEnabled =>
        DuplicateRate > 0 || DriftedDeviceRate > 0 || DropoutMeanInterval > TimeSpan.Zero;

    /// <summary>Refuses settings that do not describe a fault.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public void Validate()
    {
        if (DuplicateRate is < 0 or > 1)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The duplicate rate is a share of messages and must be between 0 and 1, not {DuplicateRate}."));
        }

        if (DriftedDeviceRate is < 0 or > 1)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The drifted device rate is a share of devices and must be between 0 and 1, not {DriftedDeviceRate}."));
        }

        if (DropoutMeanInterval > TimeSpan.Zero && DropoutDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "A dropout that lasts no time is not a dropout. Set a duration or switch the fault off.");
        }
    }
}
