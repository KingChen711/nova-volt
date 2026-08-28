using Nvm.Simulator.Formation;

namespace Nvm.UnitTests.Simulator;

/// <summary>The shape of a formation cycle, and why it has to be a shape at all.</summary>
/// <remarks>
/// Random numbers would let everything downstream appear to work while nobody could tell that a
/// projection had started computing the curve wrongly — there would be no right curve to compare
/// against, and the mistake would sleep until M8. These assertions are what makes the curve a curve.
/// </remarks>
public sealed class FormationProfileTests
{
    private static readonly FormationProfile Profile = FormationProfile.Default;

    [Fact]
    public void ACycleRunsThroughItsFiveStagesInOrder()
    {
        var steps = Enumerable
            .Range(0, 1081)
            .Select(minute => Profile.At(TimeSpan.FromMinutes(minute)).Step)
            .ToList();

        steps.Distinct().ShouldBe(
        [
            FormationStep.RestBeforeCharge,
            FormationStep.ConstantCurrentCharge,
            FormationStep.ConstantVoltageCharge,
            FormationStep.RestAfterCharge,
            FormationStep.Discharge,
        ]);

        // Distinct() preserves first-seen order, so the assertion above already says "in order" —
        // but only if no stage is ever revisited. This is the half that says that.
        steps.Zip(steps.Skip(1)).ShouldAllBe(pair => pair.Second >= pair.First);
    }

    [Fact]
    public void TheVoltageClimbsThroughConstantCurrentAndStopsAtTheCeiling()
    {
        var climbing = Samples(TimeSpan.FromMinutes(20), TimeSpan.FromHours(7), TimeSpan.FromMinutes(10))
            .Select(sample => sample.Volts)
            .ToList();

        climbing.Zip(climbing.Skip(1)).ShouldAllBe(pair => pair.Second > pair.First);
        climbing[^1].ShouldBeLessThan(4.20);

        // Held flat through CV — that is what makes it constant-voltage rather than a longer climb.
        Samples(TimeSpan.FromHours(8.5), TimeSpan.FromHours(10.5), TimeSpan.FromMinutes(30))
            .Select(sample => sample.Volts)
            .Distinct()
            .Count()
            .ShouldBe(1);
    }

    [Fact]
    public void TheCurrentIsHeldThroughConstantCurrentAndTailsOffThroughConstantVoltage()
    {
        Samples(TimeSpan.FromMinutes(20), TimeSpan.FromHours(7), TimeSpan.FromHours(1))
            .Select(sample => sample.Amperes)
            .Distinct()
            .ShouldBe([0.5]);

        var tail = Samples(TimeSpan.FromHours(8), TimeSpan.FromHours(11), TimeSpan.FromMinutes(30))
            .Select(sample => sample.Amperes)
            .ToList();

        tail.Zip(tail.Skip(1)).ShouldAllBe(pair => pair.Second < pair.First);
        tail[^1].ShouldBeLessThan(0.1);
    }

    [Fact]
    public void TheCellRestsAtZeroCurrentAndDischargesBelowIt()
    {
        Profile.At(TimeSpan.FromMinutes(5)).Amperes.ShouldBe(0);
        Profile.At(TimeSpan.FromHours(11.2)).Amperes.ShouldBe(0);
        Profile.At(TimeSpan.FromHours(15)).Amperes.ShouldBeLessThan(0);
    }

    [Fact]
    public void CapacityRisesWhileChargingAndFallsWhileDischarging()
    {
        var charged = Profile.At(TimeSpan.FromHours(11.4)).AmpHours;

        Profile.At(TimeSpan.FromMinutes(5)).AmpHours.ShouldBe(0);
        charged.ShouldBeGreaterThan(3.5);

        // The rest between charge and discharge moves no charge at all, which is what a rest is.
        Profile.At(TimeSpan.FromHours(11.2)).AmpHours.ShouldBe(charged, 0.001);
        Profile.At(TimeSpan.FromHours(17)).AmpHours.ShouldBeLessThan(charged);
    }

    [Fact]
    public void TheCellWarmsWithTheCurrentAndSitsAtAmbientWhenItRests()
    {
        // Temperature drifts with the current rather than with the clock. A cell that warmed while
        // resting would be a cell with a fault, and a simulator that produced one would train
        // everyone downstream to ignore the signal.
        Profile.At(TimeSpan.FromMinutes(5)).Celsius.ShouldBe(25);
        Profile.At(TimeSpan.FromHours(4)).Celsius.ShouldBeGreaterThan(30);
        Profile.At(TimeSpan.FromHours(11.2)).Celsius.ShouldBe(25);
    }

    [Fact]
    public void TheDischargeSitsOnAPlateauAndFallsAwayAtTheEnd()
    {
        // The knee is the feature a grading rule looks for, so a straight line here would make every
        // grading test downstream pass against data that has nothing to grade.
        var start = Profile.At(TimeSpan.FromHours(11.5));
        var middle = Profile.At(TimeSpan.FromHours(14.75));
        var end = Profile.At(TimeSpan.FromHours(18));

        var firstHalf = start.Volts - middle.Volts;
        var secondHalf = middle.Volts - end.Volts;

        secondHalf.ShouldBeGreaterThan(firstHalf * 2);
    }

    [Fact]
    public void ReadingOutsideTheCycleIsRefused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Profile.At(TimeSpan.FromMinutes(-1)));
        Should.Throw<ArgumentOutOfRangeException>(() => Profile.At(TimeSpan.FromHours(18.1)));
    }

    [Fact]
    public void ACycleShorterThanItsOwnStagesIsRefused()
    {
        // The stage boundaries are absolute, so an eight-hour cycle would put the cell in a discharge
        // that starts after the cycle has ended.
        Should.Throw<ArgumentOutOfRangeException>(() => new FormationProfile(TimeSpan.FromHours(8)));
    }

    [Fact]
    public void TheProfileIsAFunctionOfElapsedTimeAndNothingElse()
    {
        // The property the whole time-compression argument rests on. If reading the profile depended
        // on how often it had been read, compressing a run would change its measurements.
        foreach (var minutes in new[] { 0, 14, 15, 300, 480, 660, 690, 1000, 1080 })
        {
            var elapsed = TimeSpan.FromMinutes(minutes);

            Profile.At(elapsed).ShouldBe(Profile.At(elapsed));
        }
    }

    private static IEnumerable<FormationSample> Samples(TimeSpan from, TimeSpan to, TimeSpan step)
    {
        for (var elapsed = from; elapsed <= to; elapsed += step)
        {
            yield return Profile.At(elapsed);
        }
    }
}
