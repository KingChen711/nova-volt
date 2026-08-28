using System.Collections.Immutable;

namespace Nvm.Sparkplug;

/// <summary>A decoded birth: what the device declared, and the table that makes its later messages readable.</summary>
/// <param name="Readings">
/// The values the birth carried. A birth is not only a declaration — it also reports the current
/// value of every metric, which is what lets a listener that just connected show a full picture
/// instead of waiting for each value to change.
/// </param>
/// <param name="Aliases">The alias table for this session of this node.</param>
/// <remarks>
/// Two results from one parse. Building the table by decoding the payload a second time would work
/// and would also make it possible for the two to disagree, which is the sort of bug that only shows
/// up once the two calls are separated by a refactor.
/// </remarks>
public sealed record SparkplugBirth(
    ImmutableArray<DeviceReading> Readings,
    MetricAliasTable Aliases);
