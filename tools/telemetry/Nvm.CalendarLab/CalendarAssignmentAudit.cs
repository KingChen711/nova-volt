using Nvm.Time;

namespace Nvm.CalendarLab;

/// <summary>One timestamp together with the date produced by the deliberately naive calculation.</summary>
public readonly record struct CalendarAssignment(DateTimeOffset Instant, DateOnly NaiveDate);

/// <summary>The number and share of rows whose naive date disagrees with the production calendar.</summary>
public readonly record struct CalendarAssignmentAuditResult(long Rows, long MisassignedRows)
{
    /// <summary>The misassigned share on a 0–100 scale.</summary>
    public decimal MisassignedPercent => Rows == 0 ? 0 : 100m * MisassignedRows / Rows;
}

/// <summary>Compares an ordinary calendar-date assignment with the domain production calendar.</summary>
public static class CalendarAssignmentAudit
{
    /// <summary>Counts rows whose naive date differs from the production-day label.</summary>
    public static CalendarAssignmentAuditResult Compare(
        IEnumerable<CalendarAssignment> assignments,
        IProductionCalendar calendar,
        string siteId)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        long rows = 0;
        long misassignedRows = 0;

        foreach (var assignment in assignments)
        {
            rows++;

            if (assignment.NaiveDate != calendar.GetProductionDay(assignment.Instant, siteId).Date)
            {
                misassignedRows++;
            }
        }

        if (rows == 0)
        {
            throw new InvalidOperationException("A calendar audit with no rows proves nothing.");
        }

        return new CalendarAssignmentAuditResult(rows, misassignedRows);
    }
}
