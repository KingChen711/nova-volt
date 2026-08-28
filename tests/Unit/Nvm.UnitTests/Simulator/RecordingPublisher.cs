using System.Collections.Concurrent;
using Nvm.Simulator.Publishing;
using Nvm.Sparkplug;

namespace Nvm.UnitTests.Simulator;

/// <summary>Keeps what was published, so a run can be counted without a broker.</summary>
/// <remarks>
/// A broker would answer the question "does MQTT work", which is not in doubt and is not what any of
/// these tests are about. What is in doubt is what the plant <i>says</i> — how many measurements, sent
/// how many times, stamped with which clock — and that is decided before anything reaches a socket.
/// </remarks>
internal sealed class RecordingPublisher : ISparkplugPublisher
{
    public ConcurrentQueue<SparkplugMessage> Messages { get; } = new();

    public int Count => Messages.Count;

    /// <summary>The handler the worker installed, so a test can ask for a rebirth without a broker.</summary>
    public Func<CancellationToken, Task>? RebirthRequested { get; set; }

    /// <inheritdoc />
    public Func<ulong>? BeginSession { get; set; }

    /// <inheritdoc />
    public Func<CancellationToken, Task>? SessionRestored { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Always ready. Nothing here can lose a connection it never opened.</summary>
    public Task WaitForSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(SparkplugMessage message, CancellationToken cancellationToken)
    {
        Messages.Enqueue(message);

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
