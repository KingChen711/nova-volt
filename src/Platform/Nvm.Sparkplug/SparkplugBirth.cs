using System.Collections.Immutable;

namespace Nvm.Sparkplug;

/// <summary>A decoded birth: what the device declared, and the table that makes its later messages readable.</summary>
/// <param name="Readings">
/// The values the birth carried. A birth is not only a declaration — it also reports the current
/// value of every metric, which is what lets a listener that just connected show a full picture
/// instead of waiting for each value to change.
/// </param>
/// <param name="Aliases">The alias table for this session of this node.</param>
/// <param name="Sequence">
/// The payload <c>seq</c>. A birth carries 0, and every message after it counts up from there —
/// which is how a listener notices it missed one.
/// </param>
/// <param name="BirthDeathSequence">
/// The <c>bdSeq</c> metric, or null when the payload omitted it. It names <b>which session</b> this
/// birth opens, so a late <c>NDEATH</c> from the previous one can be told apart from the death of
/// the session now running.
/// </param>
/// <remarks>
/// Four results from one parse. Building the table by decoding the payload a second time would work
/// and would also make it possible for the two to disagree, which is the sort of bug that only shows
/// up once the two calls are separated by a refactor.
/// </remarks>
public sealed record SparkplugBirth(
    ImmutableArray<DeviceReading> Readings,
    MetricAliasTable Aliases,
    ulong? Sequence,
    ulong? BirthDeathSequence);

/// <summary>A decoded <c>NDEATH</c>: which session ended, and nothing else.</summary>
/// <param name="BirthDeathSequence">
/// The <c>bdSeq</c> the dying session was born under. Without it, a death that the broker held and
/// delivered late would mark a node stale that has already come back.
/// </param>
/// <remarks>
/// No readings. A death is the broker speaking on the node's behalf — the node is gone and has no
/// values to report. Anything a death did carry would be as old as the disconnection.
/// </remarks>
public sealed record SparkplugDeath(ulong? BirthDeathSequence);
