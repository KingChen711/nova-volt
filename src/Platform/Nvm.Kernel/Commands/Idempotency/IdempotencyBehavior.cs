namespace Nvm.Kernel.Commands.Idempotency;

/// <summary>Carries out a command once, however many times it arrives and however close together.</summary>
/// <typeparam name="TCommand">The command being handled.</typeparam>
/// <typeparam name="TResult">What handling it yields.</typeparam>
/// <param name="store">Where claims are taken and outcomes remembered.</param>
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
/// <b>Claim first, run second.</b> The key is reserved before the handler is called, not written down
/// after it returns. Writing it after is a check-then-act: two identical commands arriving at the same
/// instant both look it up, both miss, and both run. A gateway flushing a backlog does exactly that —
/// it does not resend politely one at a time.
/// </para>
/// <para>
/// <b>Claiming first is what makes the behaviour order load-bearing.</b> From here on, a command that
/// reaches this stage marks its key as taken, so validation <i>must</i> stay outermost: a malformed
/// command that got this far would claim the key, and the corrected resend — which carries the same
/// natural key — would be swallowed as a duplicate. The operator fixes the form, presses submit, sees
/// success, and nothing happens. See <see cref="KernelServiceCollectionExtensions.AddNvmKernel"/>.
/// </para>
/// <para>
/// <b>Still missing, and it is not small.</b> The claim lives as long as the process does. Across a
/// restart, or across two instances, nothing is shared and duplicates get through. Closing that needs
/// the store to be a database and the claim to commit in the same transaction as the event it guards
/// (docs/adr/ADR-023). Until then K7 holds within one process and no further, which is the truthful
/// claim and the one the tests assert.
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
        var commandType = typeof(TCommand).Name;

        var claim = await _store
            .ClaimAsync<TResult>(key, commandType, cancellationToken)
            .ConfigureAwait(false);

        if (!claim.IsGranted)
        {
            Interlocked.Increment(ref _duplicatesSuppressed);

            return claim.Outcome.Result;
        }

        TResult result;
        try
        {
            result = await continuation().ConfigureAwait(false);
        }
        catch
        {
            // Every failure path, cancellation included. A handler that threw did not happen, so its
            // key must go back to being unseen — otherwise a retry of a transient failure is mistaken
            // for a duplicate and the work is lost for good.
            //
            // CancellationToken.None on purpose: the token that got us here may be the very one that
            // was just cancelled, and a claim left neither completed nor abandoned blocks every
            // duplicate of that command until it times out.
            await _store.AbandonAsync(key, CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        // Also under None. Recording the outcome is what releases the callers already waiting on this
        // claim; abandoning them because the token was cancelled after the work was done would make
        // them redo work that succeeded.
        await _store
            .CompleteAsync(key, result, _clock.GetUtcNow(), CancellationToken.None)
            .ConfigureAwait(false);

        return result;
    }
}
