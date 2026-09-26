namespace Nvm.Contracts.Ports;

/// <summary>
/// Kiểm chữ ký điện tử cho FB khác (recipe, passport) mà không phụ thuộc FB Quality, nơi giữ chuỗi chữ ký.
/// Trả mã lý do nếu bộ chữ ký không đủ, hoặc null nếu hợp lệ.
/// </summary>
public interface ISignatureVerifier
{
    Task<string?> VerifyAsync(string siteId, IReadOnlyCollection<string> signatureIds, string subjectType, string subjectId,
        string contentSha256, IReadOnlyCollection<string> requiredRoles, string initiatorId, CancellationToken cancellationToken);
}
