namespace Nvm.Kernel.Commands.Idempotency;

/// <summary>What happened the first time a command with this key was handled.</summary>
/// <typeparam name="TResult">What the command yielded.</typeparam>
/// <param name="Result">The result produced then, replayed to every later duplicate.</param>
/// <param name="FirstHandledAt">When the original was handled.</param>
public sealed record IdempotentOutcome<TResult>(TResult Result, DateTimeOffset FirstHandledAt);

/// <summary>Remembers which commands have already been carried out.</summary>
/// <remarks>
/// <para>
/// Generic over the result rather than storing loose objects, so that an implementation which has to
/// serialize — the SQL Server one, arriving with the event store — can do so knowing the type,
/// instead of guessing at run time.
/// </para>
/// <para>
/// The timestamp is a parameter rather than something the store reads from a clock. Handing it in
/// keeps the clock in one place, where a test can move it (AGENTS.md K1).
/// </para>
/// <para>
/// The version that matters writes this record <b>inside the same transaction as the event it
/// guards</b>. Recorded outside, there is a window where the command is marked done and its effect
/// was rolled back — which is worse than no deduplication at all, because it fails closed and silent.
/// That version needs a database, so it belongs to the milestone that introduces one.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>Returns the earlier outcome for this key, or null when it has not been seen.</summary>
    /// <typeparam name="TResult">What the command yields.</typeparam>
    /// <param name="key">The command's idempotency key.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    Task<IdempotentOutcome<TResult>?> FindAsync<TResult>(
        IdempotencyKey key,
        CancellationToken cancellationToken);

    /// <summary>Records that a command was carried out, and what it produced.</summary>
    /// <typeparam name="TResult">What the command yields.</typeparam>
    /// <param name="key">The command's idempotency key.</param>
    /// <param name="commandType">Name of the command, kept for diagnosis.</param>
    /// <param name="result">What handling produced.</param>
    /// <param name="handledAt">When it was handled, taken from the ambient clock.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    Task RecordAsync<TResult>(
        IdempotencyKey key,
        string commandType,
        TResult result,
        DateTimeOffset handledAt,
        CancellationToken cancellationToken);
}
