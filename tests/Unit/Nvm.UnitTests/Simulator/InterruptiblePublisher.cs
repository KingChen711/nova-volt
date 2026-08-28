using System.Collections.Concurrent;
using Nvm.Simulator.Publishing;
using Nvm.Sparkplug;

namespace Nvm.UnitTests.Simulator;

/// <summary>A publisher a test can stand inside, in the middle of a batch.</summary>
/// <remarks>
/// The batch boundary is invisible from outside the worker, and both halves of J7 live exactly there:
/// a graceful stop must not drop what is left of a batch the channels have already been read for, and
/// a link that dies mid-batch must not let the readings it lost disappear from the reconciliation.
/// Holding one publish open is how a test gets to stand at that boundary instead of racing for it.
/// </remarks>
internal sealed class InterruptiblePublisher : ISparkplugPublisher
{
    private readonly TaskCompletionSource _caught = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _catching;
    private bool _caughtOne;
    private int _failAfter = int.MaxValue;

    /// <summary>What was published, in order.</summary>
    public ConcurrentQueue<SparkplugMessage> Messages { get; } = new();

    /// <summary>How many messages got through.</summary>
    public int Count => Messages.Count;

    /// <summary>Completes once a publish has been caught and is waiting to be let go.</summary>
    public Task Caught => _caught.Task;

    /// <summary>The handler the worker installed.</summary>
    public Func<CancellationToken, Task>? RebirthRequested { get; set; }

    /// <inheritdoc />
    public Func<ulong>? BeginSession { get; set; }

    /// <inheritdoc />
    public Func<CancellationToken, Task>? SessionRestored { get; set; }

    /// <summary>Hold the next publish open until <see cref="Release"/>.</summary>
    public void CatchNext() => _catching = true;

    /// <summary>Let the caught publish finish.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>Refuse every publish once this many messages have got through.</summary>
    /// <remarks>
    /// A dead link rather than a dropout: the worker sees <see cref="SparkplugPublishException"/>, the
    /// same thing the real publisher raises when the session is gone, and the rest of the batch it
    /// composed never goes anywhere.
    /// </remarks>
    public void FailAfter(int published) => _failAfter = published;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Always ready. Nothing here can lose a connection it never opened.</summary>
    public Task WaitForSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task PublishAsync(SparkplugMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_catching && !_caughtOne)
        {
            _caughtOne = true;
            _caught.TrySetResult();

            await _released.Task.ConfigureAwait(false);
        }

        if (Messages.Count >= _failAfter)
        {
            throw new SparkplugPublishException(
                $"The link is down, so '{message.Topic.Value}' has nowhere to go.");
        }

        Messages.Enqueue(message);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
