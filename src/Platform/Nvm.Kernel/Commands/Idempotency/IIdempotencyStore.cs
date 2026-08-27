using System.Diagnostics.CodeAnalysis;

namespace Nvm.Kernel.Commands.Idempotency;

/// <summary>What happened the first time a command with this key was handled.</summary>
/// <typeparam name="TResult">What the command yielded.</typeparam>
/// <param name="Result">The result produced then, replayed to every later duplicate.</param>
/// <param name="FirstHandledAt">When the original was handled.</param>
public sealed record IdempotentOutcome<TResult>(TResult Result, DateTimeOffset FirstHandledAt);

/// <summary>The answer to "may I handle this command?".</summary>
/// <typeparam name="TResult">What the command yields.</typeparam>
/// <remarks>
/// Two answers and no third. Either the caller now holds the claim and must finish it, or the work is
/// already done and here is what it produced. A caller that arrives while someone else holds the claim
/// does not get an answer at all until that someone finishes — see
/// <see cref="IIdempotencyStore.ClaimAsync{TResult}"/>.
/// </remarks>
public sealed class IdempotencyClaim<TResult>
{
    /// <summary>Builds a claim.</summary>
    /// <param name="outcome">
    /// What the first handling produced, or null to grant the claim to the caller. Prefer the named
    /// factories on <see cref="IdempotencyClaim"/>, which say which of the two this is.
    /// </param>
    public IdempotencyClaim(IdempotentOutcome<TResult>? outcome) => Outcome = outcome;

    /// <summary>What the first handling produced, when there was one.</summary>
    public IdempotentOutcome<TResult>? Outcome { get; }

    /// <summary>True when the caller must do the work; false when it must replay <see cref="Outcome"/>.</summary>
    [MemberNotNullWhen(false, nameof(Outcome))]
    public bool IsGranted => Outcome is null;
}

/// <summary>Names the two answers, so that call sites read as what they mean.</summary>
/// <remarks>
/// Non-generic on purpose. The factories cannot live on <see cref="IdempotencyClaim{TResult}"/>
/// itself — static members on a generic type force every caller to spell the type argument out, which
/// is what CA1000 is about.
/// </remarks>
public static class IdempotencyClaim
{
    /// <summary>The caller now holds the claim and must call complete or abandon.</summary>
    /// <typeparam name="TResult">What the command yields.</typeparam>
    public static IdempotencyClaim<TResult> Granted<TResult>() => new(null);

    /// <summary>The work was already done; replay this.</summary>
    /// <typeparam name="TResult">What the command yields.</typeparam>
    /// <param name="outcome">What the first handling produced.</param>
    public static IdempotencyClaim<TResult> Replay<TResult>(IdempotentOutcome<TResult> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new IdempotencyClaim<TResult>(outcome);
    }
}

/// <summary>Lets exactly one caller carry out a command, however many ask.</summary>
/// <remarks>
/// <para>
/// <b>A claim protocol, not a lookup.</b> The obvious shape — look the key up, run the handler, write
/// the key down — is a check-then-act, and check-then-act does not survive concurrency: two identical
/// commands arriving at the same instant both look up, both miss, and both run. Dedupe that only works
/// when nothing is happening at once is not dedupe. So the decision and the reservation happen in
/// <see cref="ClaimAsync{TResult}"/>, in one step, and the outcome is written back afterwards.
/// </para>
/// <para>
/// <b>Three states, not two.</b> A key is unseen, <i>in flight</i>, or completed. The middle state is
/// the one the old shape had no room for, and the one that makes concurrency work.
/// </para>
/// <para>
/// <b>The claim holder must always finish it.</b> Success calls
/// <see cref="CompleteAsync{TResult}"/>; failure calls <see cref="AbandonAsync"/>. A claim that is
/// neither completed nor abandoned blocks every duplicate of that command until it times out — which
/// is why the failure path uses <see cref="CancellationToken.None"/> rather than the token that may
/// have just been cancelled.
/// </para>
/// <para>
/// <b>Abandon frees the key rather than remembering the failure.</b> A handler that threw did not
/// happen, so a retry must be allowed to run. Remembering it would turn a transient database blip into
/// permanent silent data loss: the resend is mistaken for a duplicate and the work is never done.
/// </para>
/// <para>
/// Generic over the result rather than storing loose objects, so that an implementation which has to
/// serialize — the SQL Server one, arriving with the event store — can do so knowing the type, instead
/// of guessing at run time.
/// </para>
/// <para>
/// The timestamp is a parameter rather than something the store reads from a clock. Handing it in
/// keeps the clock in one place, where a test can move it (AGENTS.md K1).
/// </para>
/// <para>
/// <b>What is still missing, and it is not small.</b> The version that matters claims the key and
/// writes the event it guards <b>in the same transaction</b>. Until then the claim is durable only for
/// as long as the process is, and a claim recorded outside the transaction can be marked done while
/// its effect was rolled back. That version needs a database and an effect worth committing, so it
/// belongs to the milestone that introduces both (docs/adr/ADR-023).
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>Reserves the key for handling, or hands back what the first handling produced.</summary>
    /// <typeparam name="TResult">What the command yields.</typeparam>
    /// <param name="key">The command's idempotency key.</param>
    /// <param name="commandType">Name of the command, kept for diagnosis and for the mismatch guard.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    /// <returns>
    /// <see cref="IdempotencyClaim.Granted{TResult}"/> when the caller must do the work, or a replay of
    /// the earlier outcome. When another caller holds the claim, this waits for that caller to finish:
    /// if it succeeded the result is replayed, and if it gave up the claim is contested again.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The key was first claimed by a different command or a different result type. Two commands
    /// deriving one natural key is a modelling fault upstream, and the only useful thing to do with it
    /// is say so loudly rather than replay a wrongly-typed result into unrelated code.
    /// </exception>
    Task<IdempotencyClaim<TResult>> ClaimAsync<TResult>(
        IdempotencyKey key,
        string commandType,
        CancellationToken cancellationToken);

    /// <summary>The claim holder succeeded. Records the result and releases anyone waiting.</summary>
    /// <typeparam name="TResult">What the command yields.</typeparam>
    /// <param name="key">The command's idempotency key.</param>
    /// <param name="result">What handling produced.</param>
    /// <param name="handledAt">When it was handled, taken from the ambient clock.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    Task CompleteAsync<TResult>(
        IdempotencyKey key,
        TResult result,
        DateTimeOffset handledAt,
        CancellationToken cancellationToken);

    /// <summary>The claim holder failed. Drops the claim so that a retry may take it.</summary>
    /// <param name="key">The command's idempotency key.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    Task AbandonAsync(IdempotencyKey key, CancellationToken cancellationToken);
}
