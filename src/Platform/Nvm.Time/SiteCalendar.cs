namespace Nvm.Time;

/// <summary>How one plant tells the time: its zone and its shift table.</summary>
/// <param name="SiteId">The plant, for example <c>NV1</c>.</param>
/// <param name="TimeZone">The plant's zone, resolved from an IANA id such as <c>Europe/Berlin</c>.</param>
/// <param name="Schedule">The shift table the plant runs.</param>
/// <remarks>
/// The two travel together because neither answers a question on its own. "Which shift was 23:47?"
/// needs the zone to know what the clock on the wall read and the table to know what that reading
/// means, and a plant that changed one without the other would be a plant nobody could reason about.
/// </remarks>
public sealed record SiteCalendar(string SiteId, TimeZoneInfo TimeZone, ShiftSchedule Schedule);
