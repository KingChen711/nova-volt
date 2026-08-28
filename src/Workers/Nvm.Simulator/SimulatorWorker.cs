using System.Collections.Immutable;
using Nvm.Simulator.Faults;
using Nvm.Simulator.Formation;
using Nvm.Simulator.Publishing;
using Nvm.Simulator.Reporting;

namespace Nvm.Simulator;

/// <summary>Runs the plant: one tick of wall clock, one sample period of process time.</summary>
/// <remarks>
/// <para>
/// The loop is the whole of the time-compression argument. Process time always advances by exactly
/// <see cref="SimulatorOptions.SamplePeriod"/>, so the samples fall on the same points of the cycle
/// however fast the run goes; the compression only decides how long the tick waits. Turn the
/// compression up and the run finishes sooner with an identical set of measurements.
/// </para>
/// <para>
/// Every wait goes through <see cref="TimeProvider"/> (K1). That is not a formality here — it is what
/// lets a test run an eighteen-hour cycle in a few milliseconds and count what came out.
/// </para>
/// </remarks>
public sealed partial class SimulatorWorker : BackgroundService
{
    /// <summary>Shortest gap between two answered rebirth requests.</summary>
    /// <remarks>
    /// A consumer that missed the births asks once per detected gap, so it asks thousands of times
    /// before the first answer reaches it. Answering each one would republish every DBIRTH on the
    /// line thousands of times and drown the very data the consumer is trying to read.
    /// </remarks>
    private static readonly TimeSpan RebirthCooldown = TimeSpan.FromSeconds(5);

    private readonly FormationLine _line;
    private readonly FaultInjectingPublisher _publisher;
    private readonly SimulatorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SimulatorWorker> _logger;
    private readonly SemaphoreSlim _publishing = new(1, 1);

    private DateTimeOffset _lastRebirth = DateTimeOffset.MinValue;
    private long _rebirths;

    // Which session this node has declared itself under. Null until the first declaration.
    //
    // A session is opened by the transport; what makes it USABLE is the declaration. Every consumer
    // reads DDATA against an alias table a DBIRTH gave it, so one DDATA published under a session
    // whose births have not gone out yet is a message nobody can decode - and there is no recovering
    // from it afterwards, because an alias is a number with no name attached to it.
    //
    // Read and written only under _publishing, which is what makes "declare before any data" a rule
    // rather than a race between the tick loop and whichever MQTT thread noticed the reconnect.
    private ulong? _declaredSession;

    /// <summary>Creates the worker.</summary>
    /// <param name="line">The line to run.</param>
    /// <param name="publisher">Where the messages go, and what goes wrong on the way.</param>
    /// <param name="options">How fast, and how often.</param>
    /// <param name="time">The clock. Compressed runs still go through it.</param>
    /// <param name="logger">Log.</param>
    public SimulatorWorker(
        FormationLine line,
        FaultInjectingPublisher publisher,
        SimulatorOptions options,
        TimeProvider time,
        ILogger<SimulatorWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _line = line;
        _publisher = publisher;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>How many messages the line composed, birth and data together.</summary>
    /// <remarks>
    /// What the plant meant to say. What the broker actually heard is larger by however many
    /// duplicates were injected and smaller by whatever the link refused, and both of those numbers
    /// live on the publisher — see <see cref="Faults.FaultInjectingPublisher.PublishedMessages"/>.
    /// </remarks>
    public long LogicalMessageCount { get; private set; }

    /// <summary>Measurements the plant took that the link never carried.</summary>
    /// <remarks>
    /// <para>
    /// A batch is composed under one session and published one message at a time, and two things can
    /// still cut into it: the session ending between two messages, and a publish that throws. What is
    /// left has been measured and will never be sent — an unbuffered device losing what is in flight
    /// when its cable is pulled, which is a real thing for this simulator to do.
    /// </para>
    /// <para>
    /// It is a <b>diagnosis</b>, never a correction. Those readings stay in
    /// <see cref="Formation.FormationLine.MeasurementCount"/>, so D1 and D3 go red over them; taking
    /// them off the left-hand side is precisely how an oracle stops being able to see a loss. This
    /// counter only says which side of the wire the loss happened on, and D1 and D3 require it to be
    /// zero.
    /// </para>
    /// </remarks>
    public long AbandonedMeasurements { get; private set; }

    /// <summary>How far the plant has got.</summary>
    public TimeSpan ProcessElapsed { get; private set; }

    /// <summary>How many times this node re-declared itself because a consumer asked.</summary>
    public long Rebirths => Interlocked.Read(ref _rebirths);

    /// <summary>True once the line is online and the tick loop is armed.</summary>
    /// <remarks>
    /// The plant is not running until its timer exists, and until then a tick that arrives is a tick
    /// nobody is holding. Real time never delivers one that early; a test driving a fake clock can,
    /// so this says when the answer to "has it started" is yes rather than leaving it to be guessed.
    /// </remarks>
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public override void Dispose()
    {
        _publishing.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();

        // Set before connecting, so a request that arrives with the very first subscription is
        // answered rather than dropped on the floor.
        _publisher.RebirthRequested = RepublishBirthsAsync;

        // A reconnect re-declares the node for the same reason a rebirth does - every consumer is
        // holding alias tables from a session that no longer exists - but it also needs a new bdSeq,
        // which a rebirth must never take.
        _publisher.BeginSession = _line.BeginSession;
        _publisher.SessionRestored = RepublishAfterReconnectAsync;

        await _publisher.ConnectAsync(stoppingToken).ConfigureAwait(false);

        LineOnline(
            _logger,
            _line.Path.Value,
            _line.ChannelCount,
            _options.SamplePeriod,
            _options.TimeCompression);

        await DeclareAsync(stoppingToken).ConfigureAwait(false);

        // A timer armed once, not a delay re-armed every pass. Two reasons, and the second is the one
        // that matters: a delay restarted after the work is done makes the period the interval plus
        // however long the publishing took, so the message rate drifts below what the settings claim —
        // and the load numbers of D2 would be measuring a slower plant than the one on paper. The
        // first is that between two delays there is a moment with no timer at all, and a tick due in
        // that moment is a tick lost.
        using var ticker = new PeriodicTimer(_options.TickInterval, _time);

        // Written once before the loop, and this one is allowed to throw. An unwritable report path
        // is worth failing on now rather than an hour from now, when the run is over and the number
        // that was supposed to prove D1 turns out never to have been recorded.
        RunReportFile.Write(_options.ReportPath, Report());

        var lastReport = _time.GetUtcNow();

        IsRunning = true;

        try
        {
            while (await ticker.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await AdvanceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (SparkplugPublishException exception)
                {
                    // The link died between the session gate and the packet. The plant does not stop
                    // for that (N15): the next tick waits for the next session and carries on. What
                    // was in flight is gone, which is what an unbuffered device loses when its cable
                    // is pulled - the simulator does not pretend to have held it, and it does not
                    // pretend not to have measured it either. See AbandonedMeasurements.
                    PublishFailed(_logger, exception, _line.Path.Value);
                }

                var now = _time.GetUtcNow();

                if (now - lastReport >= _options.ReportInterval)
                {
                    lastReport = now;
                    TryWriteReport();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a fault. The plant stopping is the normal end of a run.
        }

        IsRunning = false;

        // CancellationToken.None: stoppingToken is already cancelled by the time execution reaches
        // here, and a dropout in progress is a gap rather than a loss — the messages are on the
        // device and the device will send them. The readings they carry are already counted, so
        // passing the cancelled token would strand them and end the run reporting measurements the
        // broker was never even offered.
        try
        {
            await _publisher.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SparkplugPublishException exception)
        {
            // A run that ends while the broker is unreachable still has a report to write, and that
            // report is the left-hand side of D1. Losing it to a failed flush would throw away the
            // measurement count in order to complain about the link.
            PublishFailed(_logger, exception, _line.Path.Value);
        }

        TryWriteReport();

        LineStopped(
            _logger,
            _line.Path.Value,
            ProcessElapsed,
            LogicalMessageCount,
            _line.MeasurementCount);
    }

    // Source-generated for the reason CA1873 gives: a TimeSpan argument boxes, and a log line that
    // allocates whether or not anybody is listening is a cost paid on every run.
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Line {Line} online: {Channels} channels, one sample per {Sample} of plant time, {Compression}x")]
    private static partial void LineOnline(ILogger logger, string line, int channels, TimeSpan sample, double compression);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Line {Line} stopped after {Elapsed} of plant time: {Messages} messages, {Measurements} measurements")]
    private static partial void LineStopped(ILogger logger, string line, TimeSpan elapsed, long messages, long measurements);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Could not write the run report to {Path}. Measurements so far: {Measurements}, duplicates: {Duplicates}")]
    private static partial void ReportNotWritten(ILogger logger, Exception error, string path, long measurements, long duplicates);

    private RunReport Report() =>
        new(
            _line.Path.Value,
            _time.GetUtcNow(),
            ProcessElapsed,
            _line.ChannelCount,
            _line.MeasurementCount,
            AbandonedMeasurements,
            LogicalMessageCount,
            _publisher.DuplicateMessages,
            _publisher.PublishedMessages,
            _line.DriftedDeviceCount,
            _publisher.Dropouts,
            _publisher.HeldHighWater,
            _options.Faults.AnyEnabled);

    // Every write after the first one is best-effort, and the counts go into the log line when it
    // fails. A disk hiccup forty minutes into a one-hour run should not throw the run away, and the
    // number is what the reconciliation needs — the file is only how it usually travels.
    private void TryWriteReport()
    {
        var report = Report();

        try
        {
            RunReportFile.Write(_options.ReportPath, report);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportNotWritten(_logger, error, _options.ReportPath, report.LogicalMeasurements, report.DuplicateMessages);
        }
    }

    // Same republication as a rebirth, minus the cooldown. The cooldown exists to absorb a host
    // asking repeatedly; a reconnect is not a request and happens once per dropped connection, so
    // rate-limiting it would leave the node silent in exactly the case it must not be.
    private Task RepublishAfterReconnectAsync(CancellationToken cancellationToken) =>
        DeclareAsync(cancellationToken);

    // Declares the node when the session it is publishing under has not been declared yet.
    //
    // Called from two places that must not disagree: the reconnect handler, so a recovered link is
    // usable at once, and the top of every tick, so it is still true if that handler never ran or
    // failed half way. Idempotent by construction - the second caller finds the session already
    // declared and does nothing.
    private async Task DeclareAsync(CancellationToken cancellationToken)
    {
        await _publishing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await DeclareLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _publishing.Release();
        }
    }

    // One tick of the plant, composed and published without letting go of the lock in between.
    //
    // Composing outside it was a real defect rather than a theoretical one: Advance moves the
    // sequence counter and every channel's deadband state, Connect resets both, and the rebirth and
    // reconnect handlers arrive on MQTT threads. Two of them running at once interleaves the seq
    // stream of one session with another's, and a consumer counting seq sees gaps that never
    // happened - then asks for a rebirth over each one.
    private async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        // Waited for OUTSIDE the lock, and that placement is the whole design. Parking here while
        // holding it would hold the lock the node needs in order to declare itself on the session
        // being waited for - the tick would be waiting for something that could not happen until the
        // tick let go. Above the lock, a dead link simply parks the plant until the link is back.
        await _publisher.WaitForSessionAsync(cancellationToken).ConfigureAwait(false);

        await _publishing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await DeclareLockedAsync().ConfigureAwait(false);

            // No token from here down, and that is the shutdown rule rather than an oversight.
            // Advance READS the channels: by the time it returns the deadbands have moved and the
            // plant has measured. A stop that dropped what was left of the batch would leave those
            // readings on D1's left-hand side with nothing ever arriving on the right. A tick that
            // has composed finishes; the loop stops before the NEXT tick, where the plant has
            // measured nothing yet and stopping costs nothing.
            ProcessElapsed += _options.SamplePeriod;

            await PublishLockedAsync(_line.Advance(ProcessElapsed)).ConfigureAwait(false);
        }
        finally
        {
            _publishing.Release();
        }
    }

    private async Task DeclareLockedAsync()
    {
        var session = _line.BirthDeathSequence;

        if (_declaredSession == session)
        {
            return;
        }

        await PublishLockedAsync(_line.Connect(ProcessElapsed)).ConfigureAwait(false);

        // Set last, so a declaration that failed part way through is attempted again on the next
        // tick instead of being remembered as done.
        _declaredSession = session;
    }

    // Re-declares the whole node: NBIRTH, then a DBIRTH for every channel, exactly as at connect.
    //
    // Republished, not re-measured. The readings carry the device clock the channel was declared at,
    // so they are the same natural keys the first declaration carried: the database stores them once
    // and the plant measured them once. FormationChannel.Declare is where that is decided, and it is
    // decided by the instant rather than here - a rebirth counted a second time would put D1's left
    // side above its right by one full DBIRTH per channel per rebirth, measured at exactly -48 on an
    // eight-channel line.
    private async Task RepublishBirthsAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        if (now - _lastRebirth < RebirthCooldown)
        {
            return;
        }

        _lastRebirth = now;
        Interlocked.Increment(ref _rebirths);

        RebirthAnswered(_logger, _line.Path.Value, _line.ChannelCount);

        // Connect() is inside the lock, not just the publishing. It resets the sequence counter and
        // the per-channel cycle state, so composing it while Advance is mid-loop would interleave two
        // seq streams and a consumer counting them would see gaps that never happened.
        await _publishing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await PublishLockedAsync(_line.Connect(ProcessElapsed)).ConfigureAwait(false);
        }
        finally
        {
            _publishing.Release();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rebirth requested: line {Line} re-declaring itself and its {Channels} channels")]
    private static partial void RebirthAnswered(ILogger logger, string line, int channels);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Line {Line} could not reach the broker; the plant keeps running and the next session carries on")]
    private static partial void PublishFailed(ILogger logger, Exception error, string line);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Line {Line} abandoned the rest of a batch numbered under session {Session}, which has ended")]
    private static partial void SessionEndedMidBatch(ILogger logger, string line, ulong session);

    // SemaphoreSlim is not reentrant, so the entry points acquire and this one assumes.
    //
    // Takes no cancellation token, on purpose. Everything in this array has already been measured, so
    // there is no point after composition at which stopping is free - the run stops between ticks
    // instead. A publish that has started is likewise allowed to finish, because its OUTCOME is what
    // the report has to describe: cancelling mid-await leaves a message the broker may well have
    // stored and this process unable to say either way, and neither D1 nor D3 has any way to
    // represent "probably delivered".
    private async Task PublishLockedAsync(ImmutableArray<ComposedMessage> messages)
    {
        var session = _line.BirthDeathSequence;

        // Composed, therefore counted - not "accepted by the broker". The fault injector answers a
        // publish during a simulated dropout by holding the message in memory and returning success,
        // so a counter moved on a successful return would be claiming delivery for something still
        // sitting on the device.
        LogicalMessageCount += messages.Length;

        for (var index = 0; index < messages.Length; index++)
        {
            // A batch is composed under one session and is only valid under that one. The link can
            // die and come back while this loop is between two messages - it is not holding a
            // thread - and what is left was numbered by the session that ended: publishing it now
            // would put a stale seq in front of the new session's NBIRTH, which is exactly the gap
            // a consumer asks for a rebirth over. A message belonging to a session nobody is
            // counting any more is not publishable.
            if (_line.BirthDeathSequence != session)
            {
                SessionEndedMidBatch(_logger, _line.Path.Value, session);
                Abandon(messages, index);

                return;
            }

            try
            {
                await _publisher.PublishAsync(messages[index].Message, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (SparkplugPublishException)
            {
                // The message that threw is abandoned along with the rest of the batch. The broker
                // may or may not have stored it, and a report that guessed either way would be
                // inventing the number D1 compares against rows - so it is named as lost, which
                // makes the gate red and says which side of the wire to look at.
                Abandon(messages, index);

                throw;
            }
        }
    }

    // Never subtracted from anything. The readings stay where the channels put them, so the
    // reconciliation fails over them; this only records how much of that failure happened before the
    // broker rather than after it.
    private void Abandon(ImmutableArray<ComposedMessage> messages, int from)
    {
        for (var index = from; index < messages.Length; index++)
        {
            AbandonedMeasurements += messages[index].Measurements;
        }
    }
}
