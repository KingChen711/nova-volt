using System.Collections.Frozen;

namespace Nvm.Time;

/// <summary>A directory built from an explicit list of plants.</summary>
/// <remarks>
/// <para>
/// For tests, and for a host that has no factory model to read yet. It is deliberately something a
/// caller has to <b>construct</b> with plants named one by one: the production path resolves a plant's
/// zone from the factory model
/// (<c>Nvm.FactoryModel.Time.FactoryModelSiteCalendarDirectory</c>), because that is the one place a
/// new plant is added and therefore the only place the answer cannot go stale.
/// </para>
/// <para>
/// If this type ever appears in a host's composition root next to a loaded factory model, that is the
/// second lookup table this design exists to avoid.
/// </para>
/// </remarks>
public sealed class InMemorySiteCalendarDirectory : ISiteCalendarDirectory
{
    private readonly FrozenDictionary<string, SiteCalendar> _calendars;

    /// <summary>Builds a directory over a fixed set of plants.</summary>
    /// <param name="calendars">The plants this directory knows.</param>
    /// <exception cref="ArgumentException">Two entries name the same plant.</exception>
    public InMemorySiteCalendarDirectory(IEnumerable<SiteCalendar> calendars)
    {
        ArgumentNullException.ThrowIfNull(calendars);

        _calendars = calendars.ToFrozenDictionary(calendar => calendar.SiteId, StringComparer.Ordinal);
    }

    /// <summary>Builds a directory over a fixed set of plants.</summary>
    public InMemorySiteCalendarDirectory(params SiteCalendar[] calendars)
        : this((IEnumerable<SiteCalendar>)calendars)
    {
    }

    /// <inheritdoc />
    public SiteCalendar? Find(string siteId) =>
        siteId is null ? null : _calendars.GetValueOrDefault(siteId);
}
