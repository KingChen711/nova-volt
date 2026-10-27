namespace Nvm.Hosting;

/// <summary>Hai health check tag mà hệ thống này dùng, và ranh giới giữa chúng.</summary>
/// <remarks>
/// <para>
/// Liveness và readiness trả lời hai câu hỏi khác nhau, và một probe thuộc về đúng một trong hai:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>live</b> — "process này đang sống, đừng restart tôi". Không thứ gì vươn ra ngoài process
///     được phép xuất hiện ở đây. Một dependency bị down không phải là lý do để giết một service khỏe
///     mạnh, và một orchestrator restart nó sẽ làm outage tệ hơn bằng cách cộng thêm cold start.
///   </description></item>
///   <item><description>
///     <b>ready</b> — "dependency của tôi đang reachable, hãy gửi traffic cho tôi". Đây là nơi một
///     outage thuộc về: instance bước ra khỏi vòng xoay và tự quay lại khi dependency hồi phục.
///   </description></item>
/// </list>
/// <para>
/// Khai báo ở đây thay vì gõ tay ở từng chỗ đăng ký vì tag chính là toàn bộ cơ chế routing:
/// <c>MapHealthChecks</c> lọc theo nó, và một probe gắn tag <c>"redy"</c> sẽ âm thầm biến mất khỏi cả
/// hai endpoint. Không có gì cảnh báo, và check đó đơn giản là không bao giờ chạy nữa.
/// </para>
/// </remarks>
public static class HealthTags
{
    /// <summary>Bản thân process đang chạy. Chỉ các check trong process.</summary>
    public const string Live = "live";

    /// <summary>Instance này có thể phục vụ traffic. Nơi các check dependency thuộc về.</summary>
    public const string Ready = "ready";
}
