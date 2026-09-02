using Nvm.Time;

namespace Nvm.CalendarLab;

/// <summary>Một timestamp cùng với date được tạo ra bởi phép tính cố tình naive.</summary>
public readonly record struct CalendarAssignment(DateTimeOffset Instant, DateOnly NaiveDate);

/// <summary>Số lượng và tỷ lệ các row có naive date không khớp với production calendar.</summary>
public readonly record struct CalendarAssignmentAuditResult(long Rows, long MisassignedRows)
{
    /// <summary>Tỷ lệ bị gán sai trên thang 0–100.</summary>
    public decimal MisassignedPercent => Rows == 0 ? 0 : 100m * MisassignedRows / Rows;
}

/// <summary>So sánh một phép gán calendar-date thông thường với production calendar của domain.</summary>
public static class CalendarAssignmentAudit
{
    /// <summary>Đếm các row có naive date khác với nhãn production-day.</summary>
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
