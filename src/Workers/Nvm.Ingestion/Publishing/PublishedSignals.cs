using System.Collections.Frozen;

namespace Nvm.Ingestion.Publishing;

/// <summary>Lists signal codes eligible to become business facts rather than observations.</summary>
/// <remarks>
/// <para>
/// scope.md §5.5 draws the line and this type is where it is drawn in code: <i>"if it changes the
/// business state of a production unit it is a domain event; if it is continuous observation it is
/// telemetry"</i>. A formation channel reporting voltage every few seconds is the second kind. The
/// capacity a cycle finished at is the first — it may later grade the cell.
/// </para>
/// <para>
/// <b>Finality is carried by the signal code and by nothing else.</b> A cycler reporting
/// <c>Formation/Capacity</c> every few seconds and a test station reporting the capacity a cell
/// finished at are two different signals, so the plant gives them two different names and only the
/// second is ever listed here. Deciding it some other way — "it has a unit id, so somebody must have
/// evaluated it" — holds only while raw readings have no unit: the moment M7 maps a channel to the
/// cell sitting in it, every point on the curve acquires one and the whole curve walks onto the bus.
/// </para>
/// <para>
/// A row still has to name the unit it is about before it can be announced, but that is a
/// completeness check on an event that is already a business fact, not the test for whether it is
/// one.
/// </para>
/// <para>
/// Publishing everything would put thousands of events a second on a bus that exists to carry
/// decisions, and would do it silently: nothing fails, the broker simply fills, and the event store
/// becomes the time-series database it was deliberately kept separate from. Publishing nothing is
/// the safe default because the telemetry is already stored either way — the reading is never lost,
/// only unannounced.
/// </para>
/// </remarks>
public sealed class PublishedSignals
{
    private readonly FrozenSet<string> _signalCodes;

    /// <summary>Creates the filter from configured signal codes.</summary>
    /// <param name="signalCodes">Signal codes to publish, exactly as the plant spells them.</param>
    public PublishedSignals(IEnumerable<string>? signalCodes)
    {
        _signalCodes = (signalCodes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            // Ordinal, like every other comparison of a plant identifier in this system. The signal
            // code goes into the natural key unnormalised (C04), so a case-insensitive match here
            // would publish an event whose SignalCode never equals the one that was configured.
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Nothing is published. The default, and what a plant that has not decided should run.</summary>
    public static PublishedSignals None { get; } = new([]);

    /// <summary>How many signal codes are on the list.</summary>
    public int Count => _signalCodes.Count;

    /// <summary>Whether a reading of this signal is announced on the bus.</summary>
    /// <param name="signalCode">The metric name as the device declared it.</param>
    public bool Includes(string signalCode) => _signalCodes.Contains(signalCode);
}
