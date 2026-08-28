namespace Nvm.Ingestion;

/// <summary>How much the device's own clock can be trusted for this reading.</summary>
/// <remarks>
/// <para>
/// A flag rather than a filter. The technical reflex is to refuse data that is wrong, and applied
/// here it means a dead twenty-thousand-dong CMOS battery erases a line's entire traceability
/// record — silently, until an auditor asks. PLC clocks drift constantly: batteries die, NTP does
/// not reach the OT layer, a board is replaced and comes up at its factory default.
/// </para>
/// <para>
/// What makes the flag safe is that <c>gateway_timestamp</c> exists and is trustworthy. The reading
/// is stored, marked, and shown with a badge; a person fixes the clock, and the system loses nothing
/// while it waits (N15).
/// </para>
/// </remarks>
public enum ClockQuality
{
    /// <summary>Device and gateway clocks agree within the threshold.</summary>
    Good,

    /// <summary>They disagree by more than the threshold. The reading is kept and flagged.</summary>
    Drifted,

    /// <summary>The source has no device clock at all, so there is nothing to compare.</summary>
    /// <remarks>
    /// Not reachable from the Sparkplug path: C02 refuses a metric with no timestamp anywhere,
    /// because <c>device_timestamp</c> is part of the natural key (scope.md §7.2) and a reading
    /// without one could never recognise itself as a duplicate. The real source is the CSV file drop
    /// in C15, where an end-of-line tester exports rows and no device clock was ever involved.
    /// </remarks>
    Unknown,
}

/// <summary>Compares the device clock against the gateway clock.</summary>
public static class ClockQualityClassifier
{
    /// <summary>The default tolerance before a device clock is called drifted (scope.md §7.3).</summary>
    /// <remarks>
    /// Five minutes is wide enough that ordinary NTP wander and network latency never trip it, and
    /// narrow enough that the failures worth naming — a battery-dead PLC sitting hours or years off
    /// — cannot hide inside it. It is a threshold on <b>disagreement</b>, not on lateness: a reading
    /// buffered for an hour by store-and-forward is still Good, because the two clocks still agree
    /// about when it was taken.
    /// </remarks>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMinutes(5);

    /// <summary>Classifies one reading.</summary>
    /// <param name="deviceTimestamp">When the device says it took the reading, if it says.</param>
    /// <param name="gatewayTimestamp">When the gateway received the publish carrying it.</param>
    /// <param name="threshold">How far apart the two may be and still be called Good.</param>
    /// <returns>The quality to store alongside all three timestamps.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The threshold is negative.</exception>
    public static ClockQuality Classify(
        DateTimeOffset? deviceTimestamp,
        DateTimeOffset gatewayTimestamp,
        TimeSpan threshold)
    {
        if (threshold < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                threshold,
                "A negative tolerance would call every reading drifted.");
        }

        if (deviceTimestamp is not { } device)
        {
            return ClockQuality.Unknown;
        }

        // Absolute, so a clock running fast is as visible as one running slow. Only comparing one
        // direction would call a PLC stamping readings two hours into the future Good, and that is
        // the shape a freshly replaced board actually arrives in.
        var disagreement = device > gatewayTimestamp
            ? device - gatewayTimestamp
            : gatewayTimestamp - device;

        return disagreement > threshold ? ClockQuality.Drifted : ClockQuality.Good;
    }

    /// <summary>The value stored in <c>ts.telemetry_measurement.clock_quality</c>.</summary>
    /// <param name="quality">The classification.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a classification.</exception>
    /// <remarks>
    /// Written out rather than taken from <c>ToString</c>. The column has a CHECK constraint on
    /// these three spellings, and renaming an enum member is a refactor nobody expects to break a
    /// database write.
    /// </remarks>
    public static string ToColumnValue(this ClockQuality quality) =>
        quality switch
        {
            ClockQuality.Good => "Good",
            ClockQuality.Drifted => "Drifted",
            ClockQuality.Unknown => "Unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unknown clock quality."),
        };
}
