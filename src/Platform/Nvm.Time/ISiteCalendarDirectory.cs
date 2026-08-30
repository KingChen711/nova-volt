namespace Nvm.Time;

/// <summary>Where the production calendar gets a plant's zone and shift table from.</summary>
/// <remarks>
/// <para>
/// A port, deliberately. The authority on which zone a plant runs is the factory model
/// (<c>FactorySite.TimeZoneId</c>), and the factory model is a Functional Block — so the adapter that
/// reads it lives there and depends on this assembly, never the other way round. Putting a
/// <c>Dictionary&lt;string, TimeZoneInfo&gt;</c> in here instead would be a second lookup table, and a
/// second lookup table is one that drifts: whoever adds the third plant at M10 edits the factory model
/// and has no reason to know this file exists.
/// </para>
/// <para>
/// It is also what keeps the calendar unit-testable. A DST test needs a zone and a table, not a
/// database and a seed file.
/// </para>
/// </remarks>
public interface ISiteCalendarDirectory
{
    /// <summary>The plant's calendar, or null when this directory does not know the plant.</summary>
    /// <param name="siteId">The plant code, for example <c>DE1</c>.</param>
    /// <remarks>
    /// Null rather than a default. A plant nobody configured is not a plant in UTC running three
    /// eight-hour shifts — it is a question the system cannot answer, and answering it anyway is how
    /// a new site silently reports production against the wrong day for a month.
    /// </remarks>
    SiteCalendar? Find(string siteId);
}
