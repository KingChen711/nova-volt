using System.Collections.Immutable;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.Simulator.Formation;

/// <summary>A message the line has composed, and how much of D1's left-hand side is riding on it.</summary>
/// <param name="Message">The Sparkplug message, exactly as it will be published.</param>
/// <param name="Measurements">
/// How many measurements this message added to <see cref="FormationLine.MeasurementCount"/> when it
/// was composed. Zero for the node's own birth, which carries protocol metrics only, and zero for a
/// rebirth's <c>DBIRTH</c>, which restates readings that were counted the first time they were taken.
/// </param>
/// <remarks>
/// <para>
/// The count itself is taken where the channel is read, not here — see
/// <see cref="FormationChannel.MeasurementCount"/> for why that side of the reconciliation has to
/// describe the plant rather than the link. What this type carries is the number a message is
/// <b>responsible</b> for, so that a batch the worker could not finish can be named exactly:
/// <see cref="SimulatorWorker.AbandonedMeasurements"/> is that sum, and D1 and D3 require it to be
/// zero rather than subtracting it from anything.
/// </para>
/// <para>
/// Deliberately a separate type from <see cref="SparkplugMessage"/>: the wire message is a contract
/// shared with the gateway, and the accounting beside it belongs to the simulator alone.
/// </para>
/// </remarks>
public sealed record ComposedMessage(SparkplugMessage Message, int Measurements)
{
    /// <summary>Where the message is going. Reads through to <see cref="Message"/>.</summary>
    /// <remarks>
    /// A facade over the two parts of the message a caller ever wants, so that carrying the
    /// accounting alongside does not force every reader to unwrap it. Publishing still takes
    /// <see cref="Message"/> explicitly — the one place that must not be able to forget which of the
    /// two things it is holding.
    /// </remarks>
    public SparkplugTopic Topic => Message.Topic;

    /// <summary>The encoded payload. Reads through to <see cref="Message"/>.</summary>
    public ImmutableArray<byte> Payload => Message.Payload;
}
