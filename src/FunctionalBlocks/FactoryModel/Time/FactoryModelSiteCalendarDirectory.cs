using System.Collections.Concurrent;
using Nvm.FactoryModel.Storage;
using Nvm.Time;

namespace Nvm.FactoryModel.Time;

/// <summary>Reads a plant's time zone off the factory model revision that plant is running.</summary>
/// <remarks>
/// <para>
/// The whole point of this class is that there is <b>no second lookup table</b>. Writing
/// <c>{"NV1": "Asia/Ho_Chi_Minh", "DE1": "Europe/Berlin"}</c> somewhere is two lines and works today;
/// the cost arrives at M10, when whoever adds the third plant edits the factory model — the one place
/// a plant is defined — and has no reason to know a second list exists. The new site then computes
/// its shifts in the wrong zone, and nothing goes red.
/// </para>
/// <para>
/// <b>The revision in force, not the newest on the shelf.</b> <c>ADR-024</c> makes a staged rollout
/// normal: NV1 can be running revision 3 while DE1 is still on 1. Asking the catalog for the latest
/// document would give DE1 an answer from a document DE1 has not adopted.
/// </para>
/// <para>
/// This adapter is why the dependency runs Functional Block → <c>Nvm.Time</c> and never back.
/// <c>Nvm.Time</c> states the question (<see cref="ISiteCalendarDirectory"/>); the block that owns the
/// plant tree answers it.
/// </para>
/// </remarks>
public sealed class FactoryModelSiteCalendarDirectory : ISiteCalendarDirectory
{
    // Resolving an IANA id walks the zone database, and this is asked once per measurement on some
    // paths. Keyed by the id rather than by the plant, so activating a new revision that changes a
    // plant's zone takes effect on the next call with no invalidation to get wrong.
    private readonly ConcurrentDictionary<string, TimeZoneInfo> _zones = new(StringComparer.Ordinal);
    private readonly IActiveFactoryModel _active;

    /// <summary>Creates the directory over whatever each plant currently has activated.</summary>
    /// <param name="active">Which revision is in force at each plant.</param>
    public FactoryModelSiteCalendarDirectory(IActiveFactoryModel active) =>
        _active = active ?? throw new ArgumentNullException(nameof(active));

    /// <inheritdoc />
    /// <remarks>
    /// Null covers two different situations, and both are genuinely "no calendar": the model does not
    /// contain the plant, and the plant has not activated a revision yet. Neither is a case for
    /// guessing a zone — a plant nobody has switched on has no shifts to report against.
    /// </remarks>
    public SiteCalendar? Find(string siteId)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return null;
        }

        var current = _active.Current(siteId);

        if (current is null)
        {
            return null;
        }

        // One table for every plant today. The shift table belongs in the factory model beside the
        // zone, and M10 is the milestone that puts it there — the seam is here so that adding it is a
        // change to this line rather than to every caller.
        return new SiteCalendar(
            current.SiteId,
            _zones.GetOrAdd(current.Site.TimeZoneId, SiteTimeZone.Of),
            ShiftSchedule.Default);
    }
}
