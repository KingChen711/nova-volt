using System.Collections.Concurrent;
using Nvm.Simulator.Publishing;
using Nvm.Sparkplug;

namespace Nvm.UnitTests.Simulator;

/// <summary>Một publisher mà một test có thể đứng bên trong, ngay giữa một batch.</summary>
/// <remarks>
/// Ranh giới của batch là vô hình từ bên ngoài worker, và cả hai nửa của J7 sống đúng ngay tại đó:
/// một graceful stop không được phép làm rớt phần còn lại của một batch mà các channel đã đọc xong,
/// và một đường truyền chết giữa batch không được phép để những reading bị mất biến mất khỏi việc
/// đối soát. Giữ một publish mở là cách một test được đứng đúng tại ranh giới đó thay vì phải chạy
/// đua để bắt kịp nó.
/// </remarks>
internal sealed class InterruptiblePublisher : ISparkplugPublisher
{
    private readonly TaskCompletionSource _caught = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _catching;
    private bool _caughtOne;
    private int _failAfter = int.MaxValue;

    /// <summary>Những gì đã được publish, theo đúng thứ tự.</summary>
    public ConcurrentQueue<SparkplugMessage> Messages { get; } = new();

    /// <summary>Có bao nhiêu message đã đi qua được.</summary>
    public int Count => Messages.Count;

    /// <summary>Hoàn tất ngay khi một publish đã bị giữ lại và đang chờ được thả ra.</summary>
    public Task Caught => _caught.Task;

    /// <summary>Handler mà worker đã cài đặt.</summary>
    public Func<CancellationToken, Task>? RebirthRequested { get; set; }

    /// <inheritdoc />
    public Func<ulong>? BeginSession { get; set; }

    /// <inheritdoc />
    public Func<CancellationToken, Task>? SessionRestored { get; set; }

    /// <summary>Giữ publish kế tiếp ở trạng thái mở cho tới <see cref="Release"/>.</summary>
    public void CatchNext() => _catching = true;

    /// <summary>Cho publish đã bị giữ lại hoàn tất.</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>Từ chối mọi publish một khi đã có đủ số message này đi qua.</summary>
    /// <remarks>
    /// Một đường truyền chết chứ không phải một dropout: worker thấy <see cref="SparkplugPublishException"/>,
    /// đúng thứ mà publisher thật ném ra khi session đã mất, và phần còn lại của batch nó đã soạn sẽ
    /// không bao giờ đi tới đâu cả.
    /// </remarks>
    public void FailAfter(int published) => _failAfter = published;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Luôn sẵn sàng. Không có gì ở đây có thể mất một kết nối mà nó chưa từng mở.</summary>
    public Task WaitForSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task PublishAsync(SparkplugMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_catching && !_caughtOne)
        {
            _caughtOne = true;
            _caught.TrySetResult();

            await _released.Task.ConfigureAwait(false);
        }

        if (Messages.Count >= _failAfter)
        {
            throw new SparkplugPublishException(
                $"The link is down, so '{message.Topic.Value}' has nowhere to go.");
        }

        Messages.Enqueue(message);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
