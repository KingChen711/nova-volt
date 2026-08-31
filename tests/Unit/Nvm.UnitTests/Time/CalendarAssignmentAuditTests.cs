using Nvm.CalendarLab;

namespace Nvm.UnitTests.Time;

public sealed class CalendarAssignmentAuditTests
{
    [Fact]
    public void Compare_AfterMidnightInShiftC_CountsTheNaiveDateAsMisassigned()
    {
        var calendar = SiteCalendars.Build();
        var instant = SiteCalendars.LocalAt(SiteCalendars.HaiPhong, 2026, 8, 26, 1, 0);
        var assignments = new[]
        {
            new CalendarAssignment(instant, new DateOnly(2026, 8, 26)),
        };

        var result = CalendarAssignmentAudit.Compare(assignments, calendar, SiteCalendars.HaiPhong);

        result.Rows.ShouldBe(1);
        result.MisassignedRows.ShouldBe(1);
        result.MisassignedPercent.ShouldBe(100m);
    }

    [Fact]
    public void Compare_BeforeMidnightInShiftC_AgreesWithTheProductionDay()
    {
        var calendar = SiteCalendars.Build();
        var instant = SiteCalendars.LocalAt(SiteCalendars.HaiPhong, 2026, 8, 25, 23, 0);
        var assignments = new[]
        {
            new CalendarAssignment(instant, new DateOnly(2026, 8, 25)),
        };

        var result = CalendarAssignmentAudit.Compare(assignments, calendar, SiteCalendars.HaiPhong);

        result.Rows.ShouldBe(1);
        result.MisassignedRows.ShouldBe(0);
        result.MisassignedPercent.ShouldBe(0m);
    }

    [Fact]
    public void Compare_EmptyInput_RefusesAProofWithoutRows()
    {
        var calendar = SiteCalendars.Build();

        var thrown = Should.Throw<InvalidOperationException>(() =>
            CalendarAssignmentAudit.Compare([], calendar, SiteCalendars.HaiPhong));

        thrown.Message.ShouldBe("A calendar audit with no rows proves nothing.");
    }
}
