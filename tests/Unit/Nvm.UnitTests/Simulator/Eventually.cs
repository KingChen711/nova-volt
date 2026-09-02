using System.Diagnostics;

namespace Nvm.UnitTests.Simulator;

/// <summary>Chờ cho worker bắt kịp một clock vừa bị đẩy đi dưới nó.</summary>
/// <remarks>
/// Một fake clock tiến trên thread gọi nó, còn tick mà nó giải phóng lại được tiếp tục trên thread
/// pool, nên việc đẩy clock đi không đồng nghĩa với việc worker đã hành động theo đó. Chờ trên chính
/// tiến độ của worker thay vì chờ bằng một sleep là điều giữ cho các test này tất định thay vì chỉ
/// thường đúng.
/// </remarks>
internal static class Eventually
{
    /// <summary>Poll cho tới khi điều kiện đúng, hoặc bỏ cuộc một cách ồn ào.</summary>
    /// <param name="condition">Cái đang được chờ.</param>
    /// <param name="because">Ý nghĩa của việc điều này không bao giờ xảy ra.</param>
    /// <exception cref="TimeoutException">Điều kiện không đúng kịp lúc.</exception>
    public static async Task TrueAsync(Func<bool> condition, string because)
    {
        // Stopwatch thay vì một clock. NVM001 từ chối DateTime.UtcNow trên toàn repository, và điều
        // đó đúng: đây là deadline cho một test bị treo, không phải một thời điểm trong ngày của nhà
        // máy, và hai cái này không được phép truy cập được qua cùng một lời gọi.
        var started = Stopwatch.GetTimestamp();

        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(1);
        }
    }
}
