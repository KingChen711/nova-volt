using Nvm.Contracts.Ports;
using Nvm.Quality.Entities;
using Nvm.Quality.Ports;

namespace Nvm.Quality.Hosting;

/// <summary>Kiểm chữ ký cho FB khác bằng đúng luật của Quality (vai trò, SoD, nội dung, ý nghĩa).</summary>
public sealed class SqlSignatureVerifier(ISignatureStore signatures) : ISignatureVerifier
{
    public async Task<string?> VerifyAsync(string siteId, IReadOnlyCollection<string> signatureIds, string subjectType,
        string subjectId, string contentSha256, IReadOnlyCollection<string> requiredRoles, string initiatorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signatureIds);
        var loaded = await signatures.LoadAsync(siteId, signatureIds, cancellationToken).ConfigureAwait(false);
        if (loaded.Count != signatureIds.Distinct(StringComparer.Ordinal).Count())
        { return QualityReasonCodes.SignatureNotFound; }
        return ApprovalPolicy.Check(loaded, requiredRoles, initiatorId, subjectType, subjectId, contentSha256);
    }
}
