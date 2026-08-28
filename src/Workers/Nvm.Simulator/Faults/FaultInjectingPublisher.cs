using Nvm.Simulator.Publishing;
using Nvm.Sparkplug;

namespace Nvm.Simulator.Faults;

/// <summary>Sits between the line and the broker and misbehaves on purpose.</summary>
/// <remarks>
/// <para>
/// Always in the path, even with every rate at zero, so that a run report can state what the faults
/// did rather than leave it to be inferred from whether anybody remembered to configure them.
/// </para>
/// <para>
/// Two faults live here because both are properties of the <b>link</b>, not of the plant: a message
/// sent twice and a connection that comes and goes. The third — a wrong clock — belongs to the device
/// and is applied where the reading is taken (<see cref="DeviceClockDrift"/>).
/// </para>
/// </remarks>
public sealed partial class FaultInjectingPublisher : ISparkplugPublisher
{
    private readonly ISparkplugPublisher _inner;
    private readonly SimulatorFaults _faults;
    private readonly TimeProvider _time;
    private readonly ILogger<FaultInjectingPublisher> _logger;
    private readonly Random _dice;
    private readonly Queue<SparkplugMessage> _held = new();

    private DateTimeOffset _nextDropout = DateTimeOffset.MaxValue;
    private DateTimeOffset _reconnectAt;
    private bool _offline;

    /// <summary>Wraps a publisher.</summary>
    /// <param name="inner">Where messages go when the link is behaving.</param>
    /// <param name="faults">Which faults are on.</param>
    /// <param name="time">The clock the dropout schedule runs on.</param>
    /// <param name="logger">Log.</param>
    public FaultInjectingPublisher(
        ISparkplugPublisher inner,
        SimulatorFaults faults,
        TimeProvider time,
        ILogger<FaultInjectingPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(faults);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        faults.Validate();

        _inner = inner;
        _faults = faults;
        _time = time;
        _logger = logger;
        _dice = new Random(faults.Seed);
    }

    /// <summary>How many messages were sent a second time.</summary>
    public long DuplicateMessages { get; private set; }

    /// <summary>How many messages reached the broker, duplicates included.</summary>
    public long PublishedMessages { get; private set; }

    /// <summary>How many times the link went down.</summary>
    public long Dropouts { get; private set; }

    /// <summary>The most messages ever waiting at once for the link to come back.</summary>
    public int HeldHighWater { get; private set; }

    /// <summary>How many are waiting right now.</summary>
    public int Held => _held.Count;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);

        ScheduleNextDropout(_time.GetUtcNow());
    }

    /// <inheritdoc />
    public async Task PublishAsync(SparkplugMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var now = _time.GetUtcNow();

        if (_offline)
        {
            if (now < _reconnectAt)
            {
                Hold(message);
                return;
            }

            _offline = false;
            ScheduleNextDropout(now);
            LinkRestored(_logger, _held.Count);

            // The whole backlog at once, which is the point of the fault rather than an accident of
            // how it is written. A link coming back does not trickle: every gateway in the area
            // reconnects within the same second and empties itself into an ingestion service that has
            // just restarted. That burst is what C10's rate limit exists to survive, and a fault that
            // released the backlog gently would leave nothing for it to prove.
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (now >= _nextDropout)
        {
            _offline = true;
            _reconnectAt = now + _faults.DropoutDuration;
            Dropouts++;
            LinkLost(_logger, _faults.DropoutDuration);
            Hold(message);
            return;
        }

        await SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends everything held, whether or not the link is due back.</summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <remarks>
    /// Called at the end of a run. A dropout is a <b>gap</b>, not a loss — the messages are on the
    /// device and the device will send them — so a run that ended mid-dropout and dropped its backlog
    /// would report more measurements taken than were ever offered, and D1 would be measuring the
    /// shutdown rather than the pipeline.
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (_held.Count > 0)
        {
            await SendAsync(_held.Dequeue(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The inner publisher is owned by whoever built it, not by this wrapper, so it is not disposed
    /// here. Anything still held is the caller's to flush — see <see cref="FlushAsync"/>, which the
    /// worker calls where the ordering against the broker's own shutdown is visible.
    /// </remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Source-generated for the reason CA1873 gives: a TimeSpan argument boxes, and these fire on a
    // path that runs thousands of times a second.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Link lost, holding messages for {Duration}")]
    private static partial void LinkLost(ILogger logger, TimeSpan duration);

    [LoggerMessage(Level = LogLevel.Information, Message = "Link restored, releasing {Held} held messages")]
    private static partial void LinkRestored(ILogger logger, int held);

    private async Task SendAsync(SparkplugMessage message, CancellationToken cancellationToken)
    {
        await _inner.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        PublishedMessages++;

        if (_faults.DuplicateRate <= 0 || _dice.NextDouble() >= _faults.DuplicateRate)
        {
            return;
        }

        // The same object, sent again. Not a second message built from the same readings: that one
        // would carry a fresh seq and, if the clock had moved, a fresh device_timestamp — and a
        // reading at a new instant is a different measurement, which deduplication is right to keep.
        // The reconciliation would then come out even while never having deduplicated anything, and
        // D1 would report success on a test that ran nothing. This is R-M2-1.
        await _inner.PublishAsync(message, cancellationToken).ConfigureAwait(false);

        DuplicateMessages++;
        PublishedMessages++;
    }

    private void Hold(SparkplugMessage message)
    {
        _held.Enqueue(message);

        if (_held.Count > HeldHighWater)
        {
            HeldHighWater = _held.Count;
        }
    }

    private void ScheduleNextDropout(DateTimeOffset now)
    {
        if (_faults.DropoutMeanInterval <= TimeSpan.Zero)
        {
            _nextDropout = DateTimeOffset.MaxValue;
            return;
        }

        // Exponential gaps, which is what a Poisson process produces and what an intermittent link
        // looks like. 1 - NextDouble() lands in (0, 1] so the logarithm is always defined; the clamp
        // keeps a very unlucky draw from scheduling the next dropout past the end of time.
        var mean = _faults.DropoutMeanInterval.TotalSeconds;
        var gap = Math.Min(-Math.Log(1 - _dice.NextDouble()) * mean, mean * 100);

        _nextDropout = now + TimeSpan.FromSeconds(gap);
    }
}
