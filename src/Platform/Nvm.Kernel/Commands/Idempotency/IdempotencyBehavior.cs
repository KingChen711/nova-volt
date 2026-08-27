namespace Nvm.Kernel.Commands.Idempotency;

/// <summary>Carries out a command once, however many times it arrives.</summary>
/// <typeparam name="TCommand">The command being handled.</typeparam>
/// <typeparam name="TResult">What handling it yields.</typeparam>
/// <param name="store">Where handled keys are remembered.</param>
/// <param name="clock">The only clock. Never <c>DateTimeOffset.UtcNow</c> (AGENTS.md K1).</param>
/// <remarks>
/// <para>
/// This is AGENTS.md K7 made real. Equipment resends when it gets no acknowledgement, a recovered
/// gateway flushes its backlog, and the bus is at-least-once — so the same intention arrives two or
/// three times as a matter of normal operation. Without this stage, a step is completed twice and a
/// material lot is consumed twice, and the numbers are wrong with nothing to show for it.
/// </para>
/// <para>
/// A duplicate gets the <b>original result replayed</b> rather than an error. The caller asked for
/// something to be true; it is true; that is a success. Answering "already done" as a failure pushes
/// every caller into treating a normal condition as an exception.
/// </para>
/// <para>
/// <b>Known gap: this stops repeats, not races.</b> The key is recorded after the handler returns, so
/// two identical commands arriving at the same instant both look it up, both miss, and both run. That
/// is the sequential case handled and the concurrent one not, and it cannot be closed here — it needs
/// the store to claim the key on the way in and confirm it on the way out, inside the transaction that
/// also writes the event. Until then, "handled once" means once per arrival order, not once per key.
/// </para>
/// </remarks>
public sealed class IdempotencyBehavior<TCommand, TResult>(IIdempotencyStore store, TimeProvider clock)
    : ICommandBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    private readonly IIdempotencyStore _store = store;
    private readonly TimeProvider _clock = clock;

    /// <summary>How many duplicates this instance has short-circuited. For tests and diagnostics.</summary>
    /// <remarks>
    /// Duplicates are worth counting even though they are normal: the count going from a trickle to a
    /// flood is how a resend storm announces itself. It is a metric, not an audit entry — see
    /// <see cref="Audit.AuditBehavior{TCommand, TResult}"/> for why those are different things.
    /// </remarks>
    public int DuplicatesSuppressed => _duplicatesSuppressed;

    private int _duplicatesSuppressed;

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(
        TCommand command,
        CommandPipelineStep<TResult> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(continuation);

        var key = command.IdempotencyKey;

        var earlier = await _store.FindAsync<TResult>(key, cancellationToken).ConfigureAwait(false);

        if (earlier is not null)
        {
            Interlocked.Increment(ref _duplicatesSuppressed);
            return earlier.Result;
        }

        var result = await continuation().ConfigureAwait(false);

        // Recorded only after the handler returned. A handler that threw did not happen, so its key
        // must stay unseen — otherwise a retry of a transient failure is mistaken for a duplicate and
        // the work is lost for good.
        await _store
            .RecordAsync(key, typeof(TCommand).Name, result, _clock.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        return result;
    }
}
