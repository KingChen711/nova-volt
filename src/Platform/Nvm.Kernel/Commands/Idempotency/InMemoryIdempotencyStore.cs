using System.Collections.Concurrent;

namespace Nvm.Kernel.Commands.Idempotency;

/// <summary>
/// Keeps handled keys in process memory. Enough to develop and test against, and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// Three limits, stated plainly because an in-memory deduplication store is the kind of thing that
/// looks like it works right up until it matters:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>The memory is the process.</b> Restart the service and every key is forgotten, so the
///     backlog a gateway flushes afterwards is processed all over again.
///   </description></item>
///   <item><description>
///     <b>One process only.</b> Two instances behind a load balancer each keep their own set and
///     neither sees the other's, so a duplicate routed elsewhere gets through.
///   </description></item>
///   <item><description>
///     <b>Not in the transaction.</b> The record lands here whether or not the effect it guards was
///     committed, which is the failure mode described on <see cref="IIdempotencyStore"/>.
///   </description></item>
/// </list>
/// <para>
/// None of that is fixable here; all three need a database. This exists so the pipeline can be built
/// and proven now, and is replaced wholesale by the SQL Server version.
/// </para>
/// <para>
/// Nothing is ever evicted either, which is correct for a test and unacceptable for a long-running
/// process.
/// </para>
/// </remarks>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    /// <summary>How many keys are currently remembered. For tests and diagnostics.</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public Task<IdempotentOutcome<TResult>?> FindAsync<TResult>(
        IdempotencyKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(key.Value, out var entry))
        {
            return Task.FromResult<IdempotentOutcome<TResult>?>(null);
        }

        // The same key coming back with a different result type means two different commands derived
        // the same natural key. That is a modelling fault upstream, and returning a wrong-typed result
        // would bury it under an InvalidCastException somewhere unrelated.
        if (entry.Result is not TResult typedResult)
        {
            throw new InvalidOperationException(
                $"Idempotency key {key} was first recorded by command '{entry.CommandType}' yielding "
                + $"'{entry.Result?.GetType().Name ?? "null"}', and is now being read as '{typeof(TResult).Name}'. "
                + "Two commands are deriving the same natural key.");
        }

        return Task.FromResult<IdempotentOutcome<TResult>?>(
            new IdempotentOutcome<TResult>(typedResult, entry.HandledAt));
    }

    /// <inheritdoc />
    public Task RecordAsync<TResult>(
        IdempotencyKey key,
        string commandType,
        TResult result,
        DateTimeOffset handledAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        // TryAdd, so the first writer wins. Two threads handling the same key at once is a race the
        // in-memory store cannot prevent; letting the second overwrite the first would at least make
        // the replayed result depend on timing.
        _entries.TryAdd(key.Value, new Entry(commandType, result, handledAt));

        return Task.CompletedTask;
    }

    private sealed record Entry(string CommandType, object? Result, DateTimeOffset HandledAt);
}
