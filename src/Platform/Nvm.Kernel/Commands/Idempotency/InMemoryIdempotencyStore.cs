using System.Collections.Concurrent;

namespace Nvm.Kernel.Commands.Idempotency;

/// <summary>Giữ claim trong bộ nhớ của process. Đủ để phát triển và test, và không hơn thế.</summary>
/// <param name="clock">Đồng hồ duy nhất, dùng cho claim timeout (AGENTS.md K1).</param>
/// <param name="claimTimeout">
/// Một duplicate chờ caller đang giữ claim bao lâu. Mặc định ba mươi giây.
/// </param>
/// <remarks>
/// <para>
/// <b>Concurrency bên trong một process được xử lý ở đây và là đúng.</b> Claim được lấy bằng một lời
/// gọi <see cref="ConcurrentDictionary{TKey, TValue}.TryAdd"/> duy nhất, nên dù có bao nhiêu thread
/// đang tranh nhau cùng một khoá thì cũng chỉ đúng một thread thắng; những thread còn lại chờ trên
/// <see cref="TaskCompletionSource{TResult}"/> của người thắng và replay lại kết quả của nó.
/// </para>
/// <para>
/// Còn lại ba giới hạn, nói thẳng ra vì một store khử trùng lặp trong bộ nhớ là kiểu thứ trông như hoạt
/// động tốt cho tới đúng lúc nó quan trọng:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Bộ nhớ chính là process.</b> Restart service thì mọi claim bị quên sạch, nên backlog mà một
///     gateway đẩy lên sau đó sẽ bị xử lý lại từ đầu.
///   </description></item>
///   <item><description>
///     <b>Chỉ một process.</b> Hai instance đứng sau load balancer mỗi cái giữ một tập riêng và không
///     thấy tập của nhau, nên một duplicate bị định tuyến sang instance khác sẽ lọt qua. Cả hai giới
///     hạn đều được chốt bằng test khẳng định chúng, thay vì để lại như một comment không ai kiểm tra
///     lại.
///   </description></item>
///   <item><description>
///     <b>Không nằm trong transaction.</b> Bản ghi được lưu ở đây bất kể hiệu ứng mà nó bảo vệ có được
///     commit hay không, đây chính là kiểu lỗi được mô tả trên <see cref="IIdempotencyStore"/>.
///   </description></item>
/// </list>
/// <para>
/// Không cái nào trong số đó sửa được ở đây; cả ba đều cần một database. Class này tồn tại để pipeline
/// có thể được xây dựng và kiểm chứng ngay bây giờ, và sẽ bị thay thế toàn bộ bằng phiên bản SQL Server
/// (docs/adr/ADR-023).
/// </para>
/// <para>
/// Cũng không có gì từng bị evict, điều này đúng cho một test và không chấp nhận được cho một process
/// chạy dài hạn.
/// </para>
/// </remarks>
public sealed class InMemoryIdempotencyStore(TimeProvider clock, TimeSpan? claimTimeout = null)
    : IIdempotencyStore
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly TimeProvider _clock = clock;
    private readonly TimeSpan _claimTimeout = claimTimeout ?? TimeSpan.FromSeconds(30);

    /// <summary>Số khoá đã được đưa tới hoàn tất. Dùng cho test và chẩn đoán.</summary>
    public int Count => _entries.Values.Count(entry => entry.Settled.Task.IsCompletedSuccessfully);

    /// <summary>Số claim đang được giữ ngay lúc này mà chưa có kết quả. Dùng cho test và chẩn đoán.</summary>
    /// <remarks>
    /// Một con số chỉ tăng mãi là hình dạng của một handler không hoàn tất cũng không abandon, và mọi
    /// duplicate của các command đó đều bị kẹt lại phía sau nó.
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

            // Toàn bộ phần mutual exclusion, gói trong một dòng. TryAdd hoặc chèn được hoặc không; không
            // có khoảng hở nào giữa lúc quyết định khoá đang rảnh và lúc chiếm lấy nó.
            if (_entries.TryAdd(key.Value, new Entry(commandType)))
            {
                return IdempotencyClaim.Granted<TResult>();
            }

            if (!_entries.TryGetValue(key.Value, out var holder))
            {
                // Bị abandon giữa hai lời gọi. Khoá lại rảnh; quay vòng và tranh lại nó.
                continue;
            }

            GuardCommandType(key, holder, commandType);

            // Nếu đã hoàn tất thì trả về ngay; nếu còn đang chạy thì dừng lại chờ cho tới khi holder
            // chốt xong. Timeout chính là khác biệt giữa một duplicate đang chờ và một duplicate chờ
            // mãi mãi phía sau một handler đã treo.
            bool succeeded;
            try
            {
                succeeded = await holder.Settled.Task
                    .WaitAsync(_claimTimeout, _clock, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Ném lại kèm theo khoá. Một câu "The operation has timed out" trần trụi không nói gì
                // về command nào đang bị kẹt, mà đó lại chính là điều duy nhất đáng biết.
                throw new TimeoutException(
                    $"Idempotency key {key} has been claimed by '{holder.CommandType}' for longer than "
                    + $"{_claimTimeout}. The caller holding it neither completed nor abandoned it.");
            }

            if (!succeeded)
            {
                // Holder đã bỏ cuộc. Công việc của nó chưa xảy ra, nên caller này giờ có thể làm việc đó.
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

        // Được publish sau cùng, và các field ở trên chỉ được đọc sau khi task này hoàn tất, nên một
        // waiter quan sát thấy thành công cũng sẽ quan sát thấy cả kết quả.
        entry.Settled.TrySetResult(true);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AbandonAsync(IdempotencyKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        // Xoá trước, rồi mới đánh thức các waiter. Thứ tự ngược lại sẽ để một waiter vừa được đánh thức
        // tranh chấp một khoá vẫn chưa được giải phóng, và nó sẽ phải đi đường chậm mà không vì lý do gì.
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

    // So sánh theo kiểu khai báo thay vì pattern-match giá trị đã lưu, để một handler trả về null một
    // cách hợp lệ sẽ được replay lại là null thay vì bị báo là mismatch.
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

        // True khi holder hoàn tất, false khi nó abandon. Dùng RunContinuationsAsynchronously để việc
        // giải phóng một hàng đợi waiter không chạy tất cả chúng trên thread đang hoàn tất.
        public TaskCompletionSource<bool> Settled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object? Result { get; set; }

        public Type? ResultType { get; set; }

        public DateTimeOffset HandledAt { get; set; }
    }
}
