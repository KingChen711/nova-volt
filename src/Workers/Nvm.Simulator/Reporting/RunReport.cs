using System.Text.Json.Serialization;

namespace Nvm.Simulator.Reporting;

/// <summary>What a run says it produced. The left-hand side of D1's reconciliation.</summary>
/// <param name="LinePath">Which line the run spoke for.</param>
/// <param name="WrittenAt">When this file was last written. A stale value means the run died.</param>
/// <param name="ProcessElapsed">How far the plant got, in its own time.</param>
/// <param name="Channels">How many channels were simulated.</param>
/// <param name="LogicalMeasurements">
/// ★ The number that must equal <c>SELECT count(*)</c> on telemetry. Signals the channels actually
/// <b>took</b>, counted where the reading is taken and before anything is handed to a transport — a
/// duplicate does not add to it, a drifted clock does not add to it, and a rebirth restating a
/// reading already counted does not add to it either.
/// <para>
/// Deliberately not "what reached the broker". Both sides of this reconciliation would then sit
/// downstream of the same wire, and a run that lost its last half-batch would report a smaller left
/// side and a smaller right side and call itself exact.
/// </para>
/// </param>
/// <param name="AbandonedMeasurements">
/// Of those, how many the link never carried — a session that ended mid-batch, or a publish that
/// threw. It is a <b>diagnosis and not a correction</b>: these are still inside
/// <paramref name="LogicalMeasurements"/>, so D1 and D3 fail over them, and both require this to be
/// <b>0</b>. Subtracting it to make an equality come out even would remove the one thing the
/// reconciliation exists to detect.
/// </param>
/// <param name="LogicalMessages">Messages the line composed, whether or not they were sent.</param>
/// <param name="DuplicateMessages">Messages sent a second time on purpose.</param>
/// <param name="PublishedMessages">
/// What the broker acknowledged. Equal to <c>LogicalMessages + DuplicateMessages</c> only on a run
/// where nothing was abandoned and nothing is still held in a simulated dropout — the difference is
/// the point of keeping the three numbers apart rather than deriving one from the others.
/// </param>
/// <param name="DriftedDevices">Channels whose clock is wrong.</param>
/// <param name="Dropouts">How many times the link went down.</param>
/// <param name="HeldHighWater">The most messages ever waiting at once for the link to come back.</param>
/// <param name="FaultsEnabled">Whether any fault was switched on at all.</param>
/// <remarks>
/// <para>
/// The fault counts sit beside the totals deliberately. A reconciliation that comes out even with
/// <c>DuplicateMessages = 0</c> has not shown that deduplication works — it has shown that nothing was
/// deduplicated. That is <c>R-M2-1</c>, and the cheapest defence against it is putting the evidence
/// where whoever reads the total cannot miss it.
/// </para>
/// <para>
/// A file rather than a metric, because the reconciliation has to be readable after the run is over
/// and the process is gone.
/// </para>
/// </remarks>
public sealed record RunReport(
    string LinePath,
    DateTimeOffset WrittenAt,
    TimeSpan ProcessElapsed,
    int Channels,
    long LogicalMeasurements,
    long AbandonedMeasurements,
    long LogicalMessages,
    long DuplicateMessages,
    long PublishedMessages,
    int DriftedDevices,
    long Dropouts,
    int HeldHighWater,
    bool FaultsEnabled);

/// <summary>Serializer configuration for the run report, and nothing else.</summary>
/// <remarks>
/// Its own context for the same reason the factory model seed has one: this is a local artefact of a
/// lab run, and it changes for entirely different reasons than a wire contract does.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RunReport))]
internal sealed partial class RunReportJsonContext : JsonSerializerContext;
