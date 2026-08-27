namespace Nvm.FactoryModel.Entities;

/// <summary>A plant, with the attributes that belong to a plant rather than to a node.</summary>
/// <param name="SiteId">The code every record carries, for example <c>NV1</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="TimeZoneId">
/// IANA time zone, for example <c>Asia/Ho_Chi_Minh</c> or <c>Europe/Berlin</c>.
/// </param>
/// <param name="Root">The site's node, and through it everything below.</param>
/// <remarks>
/// <para>
/// Separate from <see cref="FactoryNode"/> because a time zone is not a property of a node — a
/// stacker does not have one. Hanging it on every node would put a nullable field on forty of them
/// so that two could use it.
/// </para>
/// <para>
/// The time zone is not decoration. Shifts run 06–14, 14–22 and 22–06 local, and a production day is
/// the calendar date on which shift A of that cycle began, so a night shift belongs to the day it
/// started on. <c>DE1</c> observes daylight saving, which makes its shift C nine hours long once a
/// year and seven hours long once a year — the reason it exists in this model at all
/// (docs/scope.md §2.3).
/// </para>
/// </remarks>
public sealed record FactorySite(string SiteId, string Name, string TimeZoneId, FactoryNode Root);
