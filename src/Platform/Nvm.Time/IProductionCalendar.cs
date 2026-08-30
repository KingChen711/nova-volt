namespace Nvm.Time;

/// <summary>Turns an instant into the production day and shift a plant would file it under.</summary>
/// <remarks>
/// <para>
/// The central domain function of M3 (docs/scope.md §9/M3). It is a <b>function</b>, not a query:
/// nothing here reads a database, and a plant's answer depends on nothing but its zone, its shift
/// table and the instant it is asked about.
/// </para>
/// <para>
/// Every method takes a plant, and none of them defaults it. A calendar answer without a site is a
/// cross-site answer, and K3 has no room for one — 05:59 in Hai Phong and 05:59 in Leipzig are eight
/// hours apart and, on two days a year, not even a fixed eight.
/// </para>
/// </remarks>
public interface IProductionCalendar
{
    /// <summary>The production day an instant belongs to at a plant.</summary>
    /// <param name="instant">The moment, as an absolute point in time.</param>
    /// <param name="siteId">The plant, for example <c>NV1</c>.</param>
    /// <exception cref="UnknownSiteException">The plant has no calendar.</exception>
    ProductionDay GetProductionDay(DateTimeOffset instant, string siteId);

    /// <summary>The shift an instant falls in at a plant.</summary>
    /// <param name="instant">The moment, as an absolute point in time.</param>
    /// <param name="siteId">The plant.</param>
    /// <exception cref="UnknownSiteException">The plant has no calendar.</exception>
    Shift GetShift(DateTimeOffset instant, string siteId);

    /// <summary>When a given shift of a given production day started and ended at a plant.</summary>
    /// <param name="day">The production day.</param>
    /// <param name="shift">The shift.</param>
    /// <param name="siteId">The plant.</param>
    /// <returns>Two absolute instants, half-open.</returns>
    /// <exception cref="UnknownSiteException">The plant has no calendar.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The plant does not run that shift.</exception>
    ShiftBoundaries GetShiftBoundaries(ProductionDay day, Shift shift, string siteId);

    /// <summary>The production day a plant is in right now.</summary>
    /// <param name="siteId">The plant.</param>
    /// <exception cref="UnknownSiteException">The plant has no calendar.</exception>
    ProductionDay CurrentProductionDay(string siteId);

    /// <summary>The shift a plant is in right now.</summary>
    /// <param name="siteId">The plant.</param>
    /// <exception cref="UnknownSiteException">The plant has no calendar.</exception>
    Shift CurrentShift(string siteId);
}
