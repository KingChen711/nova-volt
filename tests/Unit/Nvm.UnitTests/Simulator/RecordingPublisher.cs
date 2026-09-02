using System.Collections.Concurrent;
using Nvm.Simulator.Publishing;
using Nvm.Sparkplug;

namespace Nvm.UnitTests.Simulator;

/// <summary>Giữ lại những gì đã được publish, để một run có thể được đếm mà không cần broker.</summary>
/// <remarks>
/// Một broker sẽ trả lời câu hỏi "MQTT có hoạt động không", điều không hề bị nghi ngờ và không phải
/// thứ mà bất kỳ test nào ở đây quan tâm. Điều bị nghi ngờ là những gì nhà máy <i>nói</i> — bao nhiêu
/// measurement, gửi bao nhiêu lần, đóng dấu bằng đồng hồ nào — và điều đó được quyết định trước khi
/// bất cứ thứ gì chạm tới một socket.
/// </remarks>
internal sealed class RecordingPublisher : ISparkplugPublisher
{
    public ConcurrentQueue<SparkplugMessage> Messages { get; } = new();

    public int Count => Messages.Count;

    /// <summary>Handler mà worker đã cài đặt, để một test có thể yêu cầu rebirth mà không cần broker.</summary>
    public Func<CancellationToken, Task>? RebirthRequested { get; set; }

    /// <inheritdoc />
    public Func<ulong>? BeginSession { get; set; }

    /// <inheritdoc />
    public Func<CancellationToken, Task>? SessionRestored { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Luôn sẵn sàng. Không có gì ở đây có thể mất một kết nối mà nó chưa từng mở.</summary>
    public Task WaitForSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task PublishAsync(SparkplugMessage message, CancellationToken cancellationToken)
    {
        Messages.Enqueue(message);

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
