using System.Collections.Concurrent;

namespace Nvm.Kernel.Commands.Audit;

/// <summary>Giữ audit entry trong bộ nhớ của process.</summary>
/// <remarks>
/// Một bản thay thế tạm (stand-in) có cùng giới hạn với in-memory idempotency store, nhưng nghiêm
/// trọng hơn: một audit trail biến mất khi restart thì không còn là audit trail nữa. Nó tồn tại để
/// pipeline có thể được xây dựng và kiểm chứng ngay bây giờ, và sẽ được thay bằng một bảng khi đã có
/// database để lưu vào.
/// </remarks>
public sealed class InMemoryCommandAuditSink : ICommandAuditSink
{
    private readonly ConcurrentQueue<CommandAuditEntry> _entries = new();

    /// <summary>Mọi thứ đã ghi cho đến nay, cũ nhất trước.</summary>
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
