namespace Nvm.Contracts.Ports;

/// <summary>Quality trả lời lot/cuộn (hoặc đoạn cuộn) có đang bị giữ không; Material hỏi trước khi cho tiêu hao.</summary>
public interface IMaterialHoldCheck
{
    /// <summary>Mã hold đang hiệu lực chứa lot (hoặc đoạn [from, to) của cuộn), hoặc null.</summary>
    Task<string?> ActiveHoldAsync(string siteId, string lotKind, string lotId, decimal? spanFromMeter, decimal? spanToMeter,
        CancellationToken cancellationToken);
}
