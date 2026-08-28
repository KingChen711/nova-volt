using Nvm.Sparkplug;

namespace Nvm.Simulator.Publishing;

/// <summary>Where the plant's messages go.</summary>
/// <remarks>
/// An interface so that what the simulator <i>produces</i> can be tested without a broker. The
/// question "does compressing time change the measurements" is about generation and has nothing to do
/// with MQTT, and a test that needed a running EMQX to answer it would be run rarely and trusted less.
/// </remarks>
public interface ISparkplugPublisher : IAsyncDisposable
{
    /// <summary>Opens the session. Called once before anything is published.</summary>
    /// <param name="cancellationToken">Cancels the connect.</param>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Publishes one message. Fails rather than waits when there is no session.</summary>
    /// <param name="message">The topic and payload.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <exception cref="SparkplugPublishException">The link is down, or it went down mid-send.</exception>
    Task PublishAsync(SparkplugMessage message, CancellationToken cancellationToken);

    /// <summary>Waits until there is a session to publish on.</summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="PublishAsync"/> because the two belong in different places, and
    /// putting them in one place is a deadlock. A publish that waited would park its caller in the
    /// middle of a batch — and that caller is holding the lock the node needs in order to re-declare
    /// itself on the session it is waiting for.
    /// </para>
    /// <para>
    /// So the wait sits above that lock and the publish fails fast below it. What fails is one
    /// batch, composed under a session that has ended; the next one is composed under the new
    /// session, after the births.
    /// </para>
    /// </remarks>
    Task WaitForSessionAsync(CancellationToken cancellationToken);

    /// <summary>Invoked when a host asks this node to declare itself again.</summary>
    /// <remarks>
    /// <para>
    /// Set by the worker before connecting. A consumer that missed the births — it subscribed a
    /// second late, or it restarted — cannot read one single alias-only message afterwards, and
    /// nothing recovers on its own: the next <c>DBIRTH</c> is one cell-change away, which on a
    /// formation line is eighteen hours. Sparkplug's answer is <c>NCMD Node Control/Rebirth</c>, and
    /// a device that ignores it leaves that consumer blind for the rest of the run.
    /// </para>
    /// <para>
    /// A property rather than an event because the handler is asynchronous and there is exactly one
    /// of it. An event would invite two subscribers republishing the same births at once, which is
    /// the one thing a rebirth must not do.
    /// </para>
    /// </remarks>
    Func<CancellationToken, Task>? RebirthRequested { get; set; }

    /// <summary>Supplies the <c>bdSeq</c> for a session this publisher is about to open.</summary>
    /// <remarks>
    /// The last will is registered at CONNECT and cannot be changed afterwards, so the number has to
    /// be known before the socket opens - which is why the publisher pulls it rather than being told.
    /// </remarks>
    Func<ulong>? BeginSession { get; set; }

    /// <summary>Invoked after a dropped connection has been re-established.</summary>
    /// <remarks>
    /// A reconnect leaves every consumer holding alias tables and a <c>seq</c> counter belonging to a
    /// session that has ended, so the node has to declare itself again. This is the same work a
    /// rebirth does; what differs is who asked.
    /// </remarks>
    Func<CancellationToken, Task>? SessionRestored { get; set; }
}
