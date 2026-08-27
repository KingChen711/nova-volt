using System.Diagnostics;
using MassTransit;
using Nvm.Contracts.Events.FactoryModel;
using Nvm.FactoryModel;
using Nvm.FactoryModel.Commands;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.Host.Infrastructure;

/// <summary>
/// The two development-only endpoints that put a real event on the bus.
/// </summary>
/// <remarks>
/// <para>
/// Registered only when the environment is Development, the same rule <c>DotEnvLoader</c> follows.
/// An endpoint that publishes arbitrary events on request is a fine thing to have on a laptop and an
/// open door in a plant.
/// </para>
/// <para>
/// They exist because the fan-out, retry and outage evidence for M1 has to come from a publisher
/// outside the consuming process. A test that published from within the worker would exercise the
/// bus library and skip the part being claimed: that a message crosses a process boundary and lands
/// in two queues.
/// </para>
/// </remarks>
internal static partial class DevBusEndpoints
{
    private const string LoggerName = "Nvm.Host.DevBus";

    /// <summary>
    /// Says out loud that <c>published</c> is not the same as <c>received</c>.
    /// </summary>
    /// <remarks>
    /// A publish that returned without throwing means the broker acknowledged the frame, not that any
    /// consumer will ever see the message. Reading the two as the same number is the mistake this lab
    /// is built to expose, so the answer carries the warning with it.
    /// </remarks>
    private const string LostEventsNote =
        "Events lost = requested - what the consumers actually received. Count the consumer log.";

    /// <summary>Default time allowed for one publish before it is counted as failed.</summary>
    /// <remarks>
    /// A publish to a broker that is not there does not fail quickly by itself — MassTransit holds the
    /// message while it tries to reconnect, which is the behaviour that makes recovery seamless and
    /// makes an outage lab run forever. Bounding each attempt turns "eventually" into a number.
    /// </remarks>
    private static readonly TimeSpan DefaultPublishTimeout = TimeSpan.FromSeconds(2);

    public static WebApplication MapDevBusEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/dev/bus").WithTags("dev");

        group.MapPost("/activate-revision", ActivateRevisionAsync);
        group.MapPost("/burst", PublishBurstAsync);

        return app;
    }

    /// <summary>
    /// Dispatches the real command and publishes whatever event it produced.
    /// </summary>
    /// <remarks>
    /// Publishing here, in the caller, rather than inside the handler. The handler stays free of
    /// MassTransit (AGENTS.md K9), and the seam where an outbox will go in M6 is visible: right now
    /// the state change and the publish are two steps with nothing joining them, which is exactly the
    /// dual-write the outage lab measures.
    /// </remarks>
    private static async Task<IResult> ActivateRevisionAsync(
        string site,
        int revision,
        ICommandDispatcher dispatcher,
        IPublishEndpoint publishEndpoint,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LoggerName);

        var command = new ActivateFactoryModelRevisionCommand(
            ActivateFactoryModelRevisionCommand.KeyFor(site, revision),
            site,
            revision);

        FactoryModelRevisionActivated activated;

        try
        {
            activated = await dispatcher.DispatchAsync(command, cancellationToken);
        }
        catch (CommandValidationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (FactoryModelActivationException exception)
        {
            // 409, not 400. The request was well formed and the answer is still no, which is a
            // different thing for the caller to do something about.
            return Results.Conflict(new { error = exception.Message });
        }

        await publishEndpoint.Publish(activated, cancellationToken);

        Published(logger, activated.Revision, activated.SiteId, activated.EventId);

        return Results.Ok(new
        {
            activated.EventId,
            activated.SiteId,
            activated.Revision,
            activated.NodeCount,
            activated.OccurredAt,
        });
    }

    /// <summary>
    /// Publishes a numbered run of events and reports how many made it onto the bus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sequence number is carried in <c>Revision</c>, so every event in a run is distinguishable
    /// in the consumer log and the gap left by an outage can be counted rather than estimated. The
    /// events are real <c>FactoryModelRevisionActivated</c> contracts and go through the real
    /// topology and the real CloudEvents filter; what they skip is the command handler, because the
    /// handler refuses to move a plant backwards and a run of two hundred activations is not a thing
    /// a plant can do.
    /// </para>
    /// <para>
    /// It returns counts rather than logging them only. A number that exists solely in a log line is
    /// a number somebody has to find, and this one goes straight into <c>benchmarks.md</c>.
    /// </para>
    /// </remarks>
    private static async Task<IResult> PublishBurstAsync(
        string site,
        int count,
        int? delayMs,
        int? timeoutMs,
        IFactoryModelCatalog catalog,
        IPublishEndpoint publishEndpoint,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (count < 1)
        {
            return Results.BadRequest(new { error = "count must be at least 1." });
        }

        // The newest document on the shelf. This endpoint is a publisher for the chaos lab, not an
        // activation, so "which revision" only has to be a real one.
        var model = catalog.Find(catalog.LatestRevision)!;

        if (model.FindSite(site) is null)
        {
            return Results.BadRequest(new { error = $"The model document does not describe plant '{site}'." });
        }

        var logger = loggerFactory.CreateLogger(LoggerName);
        var delay = TimeSpan.FromMilliseconds(delayMs ?? 100);
        var timeout = timeoutMs is null ? DefaultPublishTimeout : TimeSpan.FromMilliseconds(timeoutMs.Value);
        var nodeCount = model.NodeCount;

        var published = 0;
        var failed = 0;
        int? firstFailure = null;
        int? lastFailure = null;
        var startedAt = Stopwatch.GetTimestamp();

        BurstStarting(logger, count, site, delay.TotalMilliseconds, timeout.TotalMilliseconds);

        for (var sequence = 1; sequence <= count; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var activated = new FactoryModelRevisionActivated(
                // Derived, not generated: the same run publishes the same ids, so a duplicate arriving
                // after the broker recovers is recognisable as one (ADR-010).
                EventId: ActivateFactoryModelRevisionCommand.KeyFor(site, sequence).Value,
                OccurredAt: clock.GetUtcNow(),
                SiteId: site,
                Revision: sequence,
                NodeCount: nodeCount,
                EquipmentPathsAdded: [],
                EquipmentPathsRemoved: []);

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(timeout);

            try
            {
                await publishEndpoint.Publish(activated, attempt.Token);
                published++;
            }
            catch (Exception exception)
            {
                // Caught and counted, never swallowed. A publish that fails silently is how a plant
                // finds out about an outage from a customer rather than from a dashboard — and it is
                // half of what D4 asks to be shown.
                failed++;
                firstFailure ??= sequence;
                lastFailure = sequence;

                PublishFailed(logger, sequence, timeout.TotalMilliseconds, exception);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        BurstFinished(logger, published, failed, count, (long)elapsed.TotalMilliseconds);

        return Results.Ok(new
        {
            site,
            requested = count,
            published,
            failed,
            firstFailure,
            lastFailure,
            elapsedMs = (long)elapsed.TotalMilliseconds,
            note = LostEventsNote,
        });
    }

    // Source-generated. CA1873, new in .NET 10, refuses an Information-level call carrying more than
    // one property: the arguments are boxed into an array before anything asks whether the level is
    // switched on. The generator emits the IsEnabled check first.
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Published revision {Revision} for {SiteId} (ce_id {EventId})")]
    private static partial void Published(ILogger logger, int revision, string siteId, Guid eventId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Burst starting: {Count} events for {SiteId}, {DelayMs} ms apart, "
            + "{TimeoutMs} ms allowed per publish")]
    private static partial void BurstStarting(
        ILogger logger,
        int count,
        string siteId,
        double delayMs,
        double timeoutMs);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Burst: publish of sequence {Sequence} failed after {TimeoutMs} ms")]
    private static partial void PublishFailed(
        ILogger logger,
        int sequence,
        double timeoutMs,
        Exception exception);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Burst finished: {Published} published, {Failed} failed, of {Count} requested "
            + "in {ElapsedMs} ms")]
    private static partial void BurstFinished(
        ILogger logger,
        int published,
        int failed,
        int count,
        long elapsedMs);
}
