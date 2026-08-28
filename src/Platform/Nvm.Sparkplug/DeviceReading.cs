namespace Nvm.Sparkplug;

/// <summary>One metric of one device, decoded out of a Sparkplug payload.</summary>
/// <param name="MetricName">
/// The metric's full name, for example <c>Formation/Voltage</c>. Always present, even when the
/// payload carried only an alias — resolving it is what <see cref="MetricAliasTable"/> is for.
/// </param>
/// <param name="Alias">The number this metric travels under, or null when it has none.</param>
/// <param name="Value">What was measured.</param>
/// <param name="DeviceTimestamp">
/// When the <b>device</b> says it took the reading. Not when the gateway received it and not when we
/// stored it — those are two other columns, and docs/scope.md §7.3 is emphatic that the three are
/// never merged.
/// </param>
/// <remarks>
/// <para>
/// <see cref="DateTimeOffset"/>, never <see cref="DateTime"/> (AGENTS.md K2). Site DE1 observes
/// daylight saving, so one hour every autumn happens twice there; a wall-clock reading with no offset
/// cannot say which of the two it was, and telemetry is retained 400 days — long enough for that
/// October hour to still be in the table when somebody investigates it.
/// </para>
/// <para>
/// The analyzers do not cover this. NVM002 refuses <see cref="DateTime"/> across
/// <c>Nvm.Contracts</c>, and this type is in <c>Nvm.Sparkplug</c>, so the guard here is
/// <c>SparkplugTimeTypeTests</c> instead.
/// </para>
/// <para>
/// A reading is deliberately <b>not</b> an event. It has no <c>SiteId</c> and no equipment path yet,
/// because a Sparkplug payload does not carry them — they live in the MQTT topic, and C03 is what
/// joins the two.
/// </para>
/// </remarks>
public sealed record DeviceReading(
    string MetricName,
    ulong? Alias,
    MetricValue Value,
    DateTimeOffset DeviceTimestamp);
