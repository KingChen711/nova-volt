using Nvm.Time;

namespace Nvm.UnitTests.Time;

public sealed class ShiftScheduleTests
{
    [Fact]
    public void Default_CoversExactlyTwentyFourHours()
    {
        // The property the whole type exists for. A table covering 23 hours would leave one hour of
        // every day belonging to no shift, and the readings taken in it would be filed under nothing
        // at all — a gap nobody notices until a monthly total comes up short.
        var covered = ShiftSchedule.Default.Definitions
            .Aggregate(TimeSpan.Zero, (total, definition) => total + definition.NominalLength);

        covered.ShouldBe(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Default_IsTheTableFromScopeSection2Point3()
    {
        ShiftSchedule.Default.Definitions.ShouldBe(
        [
            new ShiftDefinition(Shift.A, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
            new ShiftDefinition(Shift.B, new TimeOnly(14, 0), TimeSpan.FromHours(8)),
            new ShiftDefinition(Shift.C, new TimeOnly(22, 0), TimeSpan.FromHours(8)),
        ]);

        ShiftSchedule.Default.DayStart.ShouldBe(new TimeOnly(6, 0));
    }

    [Fact]
    public void Default_ShiftCIsTheOnlyOneThatCrossesMidnight()
    {
        // Every awkward case in this assembly comes from this single fact, so it is worth an
        // assertion rather than a comment: if a future table made two shifts wrap, the production day
        // rule ("the date shift A began on") would stop being enough to name a cycle.
        var wrapping = ShiftSchedule.Default.Definitions
            .Where(definition => definition.LocalStart.Add(definition.NominalLength) <= definition.LocalStart)
            .Select(definition => definition.Shift)
            .ToList();

        wrapping.ShouldBe([Shift.C]);
    }

    [Theory]
    [InlineData(6, 0, Shift.A)]
    [InlineData(13, 59, Shift.A)]
    [InlineData(14, 0, Shift.B)]
    [InlineData(21, 59, Shift.B)]
    [InlineData(22, 0, Shift.C)]
    [InlineData(23, 59, Shift.C)]
    [InlineData(0, 0, Shift.C)]
    [InlineData(5, 59, Shift.C)]
    public void ShiftAt_PutsEachClockReadingInOneShift(int hour, int minute, Shift expected)
    {
        ShiftSchedule.Default.ShiftAt(new TimeOnly(hour, minute)).Shift.ShouldBe(expected);
    }

    [Fact]
    public void ShiftAt_IsHalfOpenAtEveryBoundary()
    {
        // 14:00 belongs to B, not to both A and B. One tick earlier still belongs to A. Getting this
        // wrong double-counts one reading per shift change per machine, which is small enough to look
        // like noise and large enough to move a yield figure.
        var schedule = ShiftSchedule.Default;
        var boundary = new TimeOnly(14, 0);

        schedule.ShiftAt(boundary).Shift.ShouldBe(Shift.B);
        schedule.ShiftAt(boundary.Add(TimeSpan.FromTicks(-1))).Shift.ShouldBe(Shift.A);
    }

    [Fact]
    public void OffsetIntoDay_MeasuresFromTheOpeningShift()
    {
        var schedule = ShiftSchedule.Default;

        schedule.OffsetIntoDay(Shift.A).ShouldBe(TimeSpan.Zero);
        schedule.OffsetIntoDay(Shift.B).ShouldBe(TimeSpan.FromHours(8));
        schedule.OffsetIntoDay(Shift.C).ShouldBe(TimeSpan.FromHours(16));
    }

    [Fact]
    public void Create_RefusesATableWithAGap()
    {
        // B starts an hour after A ends. The hour from 13:00 to 14:00 belongs to nobody.
        var gapped = new[]
        {
            new ShiftDefinition(Shift.A, new TimeOnly(6, 0), TimeSpan.FromHours(7)),
            new ShiftDefinition(Shift.B, new TimeOnly(14, 0), TimeSpan.FromHours(8)),
            new ShiftDefinition(Shift.C, new TimeOnly(22, 0), TimeSpan.FromHours(8)),
        };

        Should.Throw<ArgumentException>(() => ShiftSchedule.Create(gapped));
    }

    [Fact]
    public void Create_RefusesATableThatOverlapsItself()
    {
        // A runs nine hours and B still starts at 14:00, so 14:00–15:00 belongs to two shifts. The
        // total is 25 hours, and that is the check that catches it.
        var overlapping = new[]
        {
            new ShiftDefinition(Shift.A, new TimeOnly(6, 0), TimeSpan.FromHours(9)),
            new ShiftDefinition(Shift.B, new TimeOnly(14, 0), TimeSpan.FromHours(8)),
            new ShiftDefinition(Shift.C, new TimeOnly(22, 0), TimeSpan.FromHours(8)),
        };

        Should.Throw<ArgumentException>(() => ShiftSchedule.Create(overlapping));
    }

    [Fact]
    public void Create_RefusesATableThatNamesAShiftTwice()
    {
        var duplicated = new[]
        {
            new ShiftDefinition(Shift.A, new TimeOnly(6, 0), TimeSpan.FromHours(12)),
            new ShiftDefinition(Shift.A, new TimeOnly(18, 0), TimeSpan.FromHours(12)),
        };

        Should.Throw<ArgumentException>(() => ShiftSchedule.Create(duplicated));
    }

    [Fact]
    public void Create_RefusesAnEmptyTable()
    {
        Should.Throw<ArgumentException>(() => ShiftSchedule.Create([]));
    }

    [Fact]
    public void Create_AcceptsATableThatIsNotThreeEightHourShifts()
    {
        // M10 brings a site with a different table, and the type has to be able to hold it. Two
        // twelve-hour shifts starting at 07:00 — a real pattern, and one that moves the production
        // day boundary with it.
        var twelves = ShiftSchedule.Create(
        [
            new ShiftDefinition(Shift.A, new TimeOnly(7, 0), TimeSpan.FromHours(12)),
            new ShiftDefinition(Shift.B, new TimeOnly(19, 0), TimeSpan.FromHours(12)),
        ]);

        twelves.DayStart.ShouldBe(new TimeOnly(7, 0));
        twelves.ShiftAt(new TimeOnly(6, 59)).Shift.ShouldBe(Shift.B);
        twelves.ShiftAt(new TimeOnly(7, 0)).Shift.ShouldBe(Shift.A);
    }

    [Fact]
    public void Definition_RefusesAShiftThePlantDoesNotRun()
    {
        var twelves = ShiftSchedule.Create(
        [
            new ShiftDefinition(Shift.A, new TimeOnly(7, 0), TimeSpan.FromHours(12)),
            new ShiftDefinition(Shift.B, new TimeOnly(19, 0), TimeSpan.FromHours(12)),
        ]);

        Should.Throw<ArgumentOutOfRangeException>(() => twelves.Definition(Shift.C));
    }
}
