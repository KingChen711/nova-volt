using System.Collections.Concurrent;

namespace Nvm.Kernel.Commands.Idempotency;

/// <summary>Keeps claims in process memory. Enough to develop and test against, and nothing more.</summary>
/// <param name="clock">The only clock, used for the claim timeout (AGENTS.md K1).</param>
/// <param name="claimTimeout">
/// How long a duplicate waits for the caller holding the claim. Defaults to thirty seconds.
/// </param>
/// <remarks>
/// <para>
/// <b>Concurrency inside one process is handled here and is correct.</b> The claim is taken with a
/// single <see cref="ConcurrentDictionary{TKey, TValue}.TryAdd"/>, so of any number of threads racing
/// for one key exactly one wins; the rest wait on the winner's
/// <see cref="TaskCompletionSource{TResult}"/> and replay its result.
/// </para>
/// <para>
/// Three limits remain, stated plainly because an in-memory deduplication store is the kind of thing
/// that looks like it works right up until it matters:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>The memory is the process.</b> Restart the service and every claim is forgotten, so the
///     backlog a gateway flushes afterwards is processed all over again.
///   </description></item>
///   <item><description>
///     <b>One process only.</b> Two instances behind a load balancer each keep their own set and
///     neither sees the other's, so a duplicate routed elsewhere gets through. Both limits are pinned
///     by tests that assert them rather than left as a comment nobody re-checks.
///   </description></item>
///   <item><description>
///     <b>Not in the transaction.</b> The record lands here whether or not the effect it guards was
///     committed, which is the failure mode described on <see cref="IIdempotencyStore"/>.
///   </description></item>
/// </list>
/// <para>
/// None of that is fixable here; all three need a database. This exists so the pipeline can be built
/// and proven now, and is replaced wholesale by the SQL Server version (docs/adr/ADR-023).
/// </para>
/// <para>
/// Nothing is ever evicted either, which is correct for a test and unacceptable for a long-running
/// process.
/// </para>
/// </remarks>
public sealed class InMemoryIdempotencyStore(TimeProvider clock, TimeSpan? claimTimeout = null)
    : IIdempotencyStore
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly TimeProvider _clock = clock;
    private readonly TimeSpan _claimTimeout = claimTimeout ?? TimeSpan.FromSeconds(30);

    /// <summary>How many keys have been carried through to completion. For tests and diagnostics.</summary>
    public int Count => _entries.Values.Count(entry => entry.Settled.Task.IsCompletedSuccessfully);

    /// <summary>How many claims are held right now with no outcome yet. For tests and diagnostics.</summary>
    /// <remarks>
    /// A number that only ever grows is the shape of a handler that neither completes nor abandons,
    /// and every duplicate of those commands is stuck behind it.
    /// </remarks>
    public int InFlightCount => _entries.Values.Count(entry => !entry.Settled.Task.IsCompleted);

    /// <inheritdoc />
    public async Task<IdempotencyClaim<TResult>> ClaimAsync<TResult>(
        IdempotencyKey key,
        string commandType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The whole of the mutual exclusion, in one line. TryAdd either inserts or does not; there
            // is no window between deciding the key is free and taking it.
            if (_entries.TryAdd(key.Value, new Entry(commandType)))
            {
                return IdempotencyClaim.Granted<TResult>();
            }

            if (!_entries.TryGetValue(key.Value, out var holder))
            {
                // Abandoned between the two calls. The key is free again; go round and contest it.
                continue;
            }

            GuardCommandType(key, holder, commandType);

            // Already completed and this returns at once; still in flight and this parks until the
            // holder settles it. The timeout is the difference between a duplicate waiting and a
            // duplicate waiting forever behind a handler that hung.
            bool succeeded;
            try
            {
                succeeded = await holder.Settled.Task
                    .WaitAsync(_claimTimeout, _clock, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Rethrown with the key in it. A bare "The operation has timed out" says nothing about
                // which command is stuck, and the stuck one is the only thing worth knowing.
                throw new TimeoutException(
                    $"Idempotency key {key} has been claimed by '{holder.CommandType}' for longer than "
                    + $"{_claimTimeout}. The caller holding it neither completed nor abandoned it.");
            }

            if (!succeeded)
            {
                // The holder gave up. Its work did not happen, so this caller may now do it.
                continue;
            }

            return IdempotencyClaim.Replay(OutcomeOf<TResult>(key, holder));
        }
    }

    /// <inheritdoc />
    public Task CompleteAsync<TResult>(
        IdempotencyKey key,
        TResult result,
        DateTimeOffset handledAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(key.Value, out var entry))
        {
            throw new InvalidOperationException(
                $"Idempotency key {key} was completed without being claimed. "
                + "Completing is only ever valid for the caller that holds the claim.");
        }

        entry.Result = result;
        entry.ResultType = typeof(TResult);
        entry.HandledAt = handledAt;

        // Published last, and the fields above are read only after this task completes, so a waiter
        // that observes success also observes the result.
        entry.Settled.TrySetResult(true);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AbandonAsync(IdempotencyKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        // Removed first, then waiters are woken. The other order lets a woken waiter contest a key
        // that has not been freed yet, and it would take the slow path for no reason.
        if (_entries.TryRemove(key.Value, out var entry))
        {
            entry.Settled.TrySetResult(false);
        }

        return Task.CompletedTask;
    }

    private static void GuardCommandType(IdempotencyKey key, Entry entry, string commandType)
    {
        if (!string.Equals(entry.CommandType, commandType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Idempotency key {key} was first claimed by command '{entry.CommandType}' and is now "
                + $"being claimed by '{commandType}'. Two commands are deriving the same natural key.");
        }
    }

    // Compares the declared type rather than pattern-matching the stored value, so that a handler
    // which legitimately returned null is replayed as null instead of being reported as a mismatch.
    private static IdempotentOutcome<TResult> OutcomeOf<TResult>(IdempotencyKey key, Entry entry)
    {
        if (entry.ResultType != typeof(TResult))
        {
            throw new InvalidOperationException(
                $"Idempotency key {key} was first recorded by command '{entry.CommandType}' yielding "
                + $"'{entry.ResultType?.Name ?? "nothing"}', and is now being read as "
                + $"'{typeof(TResult).Name}'. Two commands are deriving the same natural key.");
        }

        return new IdempotentOutcome<TResult>((TResult)entry.Result!, entry.HandledAt);
    }

    private sealed class Entry(string commandType)
    {
        public string CommandType { get; } = commandType;

        // True when the holder completed, false when it abandoned. RunContinuationsAsynchronously so
        // that releasing a queue of waiters does not run all of them on the completing thread.
        public TaskCompletionSource<bool> Settled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object? Result { get; set; }

        public Type? ResultType { get; set; }

        public DateTimeOffset HandledAt { get; set; }
    }
}
