namespace Nvm.Simulator.Formation;

/// <summary>What one formation channel reads at one moment of a cycle.</summary>
/// <param name="Step">Which of the five stages the cycle is in.</param>
/// <param name="Volts">Cell voltage.</param>
/// <param name="Amperes">Current. Positive charges the cell, negative discharges it.</param>
/// <param name="Celsius">Cell surface temperature.</param>
/// <param name="AmpHours">Charge accumulated so far.</param>
public readonly record struct FormationSample(
    FormationStep Step,
    double Volts,
    double Amperes,
    double Celsius,
    double AmpHours);

/// <summary>The five stages of a formation cycle, in order.</summary>
public enum FormationStep
{
    /// <summary>The cell sits after being loaded, so its open-circuit voltage can settle.</summary>
    RestBeforeCharge = 1,

    /// <summary>Constant current: the current is held and the voltage climbs.</summary>
    ConstantCurrentCharge = 2,

    /// <summary>Constant voltage: the voltage is held at the ceiling and the current tails off.</summary>
    ConstantVoltageCharge = 3,

    /// <summary>The cell rests and relaxes off its charge voltage.</summary>
    RestAfterCharge = 4,

    /// <summary>The first discharge, which is what the measured capacity comes from.</summary>
    Discharge = 5,
}

/// <summary>The shape a formation cycle has, as a function of how far into it the cell is.</summary>
/// <remarks>
/// <para>
/// A pure function of elapsed cycle time, and everything else in the simulator depends on that. It is
/// what lets a cycle be run at a thousand times speed and produce the <b>same measurements</b> — the
/// samples are taken at fixed points of process time, so compressing the wall clock changes how long
/// the run takes and nothing else.
/// </para>
/// <para>
/// <b>Not random.</b> Random values would make everything downstream appear to work while nobody could
/// tell that a projection had started computing the curve wrongly, because there would be no right
/// curve to compare against — and the mistake would sleep until M8. The voltage climbs along a charge
/// curve, the current steps between CC and CV, the temperature follows the current, and capacity is
/// the integral of it.
/// </para>
/// <para>
/// It is a shape, not a model. Nobody should predict cell chemistry from this; the point is that the
/// curve has the features a real one has — a rising CC leg, a CV tail, a discharge plateau with a knee
/// at the end — so that code reading it can be seen to be reading it correctly.
/// </para>
/// </remarks>
public sealed class FormationProfile
{
    private static readonly TimeSpan RestBefore = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChargeCcEnd = TimeSpan.FromHours(8);
    private static readonly TimeSpan ChargeCvEnd = TimeSpan.FromHours(11);
    private static readonly TimeSpan RestAfterEnd = TimeSpan.FromHours(11.5);

    private const double RestVolts = 3.00;
    private const double CeilingVolts = 4.20;
    private const double RelaxedVolts = 4.15;
    private const double EmptyVolts = 3.00;
    private const double ChargeAmperes = 0.50;
    private const double DischargeAmperes = -0.40;
    private const double AmbientCelsius = 25.0;
    private const double CelsiusPerAmpere = 14.0;

    /// <summary>The CV tail's decay constant: current falls to about 5% of the CC value by the end.</summary>
    private const double CvDecay = 3.0;

    /// <summary>The reference cycle: 18 hours, which is the middle of the 12–24 hour range.</summary>
    public static FormationProfile Default { get; } = new(TimeSpan.FromHours(18));

    /// <summary>Creates a profile of a given length.</summary>
    /// <param name="cycleDuration">How long one cell spends in the channel.</param>
    /// <exception cref="ArgumentOutOfRangeException">The cycle is shorter than its own stages.</exception>
    public FormationProfile(TimeSpan cycleDuration)
    {
        // The stage boundaries are absolute, so a cycle shorter than the last of them would put the
        // cell in a stage that ends before it starts.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cycleDuration, RestAfterEnd);

        CycleDuration = cycleDuration;
    }

    /// <summary>How long one cell spends in the channel.</summary>
    public TimeSpan CycleDuration { get; }

    /// <summary>Reads the channel at a point in the cycle.</summary>
    /// <param name="elapsed">How far into the cycle the cell is.</param>
    /// <exception cref="ArgumentOutOfRangeException">The point is outside the cycle.</exception>
    public FormationSample At(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elapsed, CycleDuration);

        if (elapsed < RestBefore)
        {
            return Sample(FormationStep.RestBeforeCharge, RestVolts, 0, 0);
        }

        if (elapsed < ChargeCcEnd)
        {
            var progress = Fraction(elapsed - RestBefore, ChargeCcEnd - RestBefore);

            // Power 0.8 rather than a straight line: a real CC leg climbs quickly out of the low-state
            // knee and then flattens as it approaches the ceiling.
            return Sample(
                FormationStep.ConstantCurrentCharge,
                RestVolts + ((CeilingVolts - RestVolts) * Math.Pow(progress, 0.8)),
                ChargeAmperes,
                ChargeAmperes * (elapsed - RestBefore).TotalHours);
        }

        var ccAmpHours = ChargeAmperes * (ChargeCcEnd - RestBefore).TotalHours;

        if (elapsed < ChargeCvEnd)
        {
            var progress = Fraction(elapsed - ChargeCcEnd, ChargeCvEnd - ChargeCcEnd);
            var hours = (ChargeCvEnd - ChargeCcEnd).TotalHours;

            // Held at the ceiling while the current decays; the accumulated charge is the integral of
            // that decay, which is why the capacity curve bends here rather than continuing straight.
            return Sample(
                FormationStep.ConstantVoltageCharge,
                CeilingVolts,
                ChargeAmperes * Math.Exp(-CvDecay * progress),
                ccAmpHours + (ChargeAmperes * hours * (1 - Math.Exp(-CvDecay * progress)) / CvDecay));
        }

        var chargedAmpHours = ccAmpHours
            + (ChargeAmperes * (ChargeCvEnd - ChargeCcEnd).TotalHours * (1 - Math.Exp(-CvDecay)) / CvDecay);

        if (elapsed < RestAfterEnd)
        {
            var progress = Fraction(elapsed - ChargeCvEnd, RestAfterEnd - ChargeCvEnd);

            return Sample(
                FormationStep.RestAfterCharge,
                CeilingVolts - ((CeilingVolts - RelaxedVolts) * (1 - Math.Exp(-4 * progress))),
                0,
                chargedAmpHours);
        }

        var discharged = Fraction(elapsed - RestAfterEnd, CycleDuration - RestAfterEnd);

        // Power 2.2 puts the knee at the end: the voltage sits on a plateau for most of the discharge
        // and then falls away quickly, which is the feature a grading rule looks for.
        return Sample(
            FormationStep.Discharge,
            RelaxedVolts - ((RelaxedVolts - EmptyVolts) * Math.Pow(discharged, 2.2)),
            DischargeAmperes,
            chargedAmpHours + (DischargeAmperes * (elapsed - RestAfterEnd).TotalHours));
    }

    private static double Fraction(TimeSpan elapsed, TimeSpan span) =>
        Math.Clamp(elapsed / span, 0, 1);

    // Temperature follows the current rather than the clock: the cell warms while charge moves through
    // it and sits at ambient while it rests. No lag is modelled, because a lag needs state and state
    // would make the profile depend on how often it was sampled — which is exactly the property that
    // has to survive time compression.
    private static FormationSample Sample(FormationStep step, double volts, double amperes, double ampHours) =>
        new(step, volts, amperes, AmbientCelsius + (CelsiusPerAmpere * Math.Abs(amperes)), ampHours);
}
