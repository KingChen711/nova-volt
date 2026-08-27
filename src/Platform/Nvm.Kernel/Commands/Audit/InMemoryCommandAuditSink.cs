using System.Collections.Concurrent;

namespace Nvm.Kernel.Commands.Audit;

/// <summary>Keeps audit entries in process memory.</summary>
/// <remarks>
/// A stand-in with the same limits as the in-memory idempotency store, and one that matters more: an
/// audit trail that disappears on restart is not an audit trail. It exists so the pipeline can be
/// built and proven now, and is replaced by a table when there is a database to put it in.
/// </remarks>
public sealed class InMemoryCommandAuditSink : ICommandAuditSink
{
    private readonly ConcurrentQueue<CommandAuditEntry> _entries = new();

    /// <summary>Everything written so far, oldest first.</summary>
    public IReadOnlyCollection<CommandAuditEntry> Entries => [.. _entries];

    /// <inheritdoc />
    public Task WriteAsync(CommandAuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        _entries.Enqueue(entry);

        return Task.CompletedTask;
    }
}
