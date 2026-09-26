using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nvm.Quality.Entities;

/// <summary>Một chữ ký điện tử đã ghi (scope §13.3).</summary>
public sealed record SignatureRecord(string SignatureId, string SubjectType, string SubjectId, string SignerId,
    string SignerRole, string Meaning, string ContentSha256, DateTimeOffset SignedAt, string PreviousHash, string Hash);

/// <summary>
/// Chuỗi hash chữ ký của một site: mỗi chữ ký băm cùng hash của chữ ký trước, nên sửa một byte bất kỳ ở nội dung
/// đã ký hoặc ở chữ ký cũ đều làm hỏng mọi hash phía sau.
/// </summary>
public static class SignatureChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    public static readonly string[] Meanings = ["Approved", "Rejected", "Reviewed", "Witnessed"];

    public static string Hash(string previousHash, string signatureId, string subjectType, string subjectId,
        string signerId, string signerRole, string meaning, string contentSha256, DateTimeOffset signedAt)
    {
        var text = string.Join('\u001f', previousHash, signatureId, subjectType, subjectId, signerId, signerRole,
            meaning, contentSha256, signedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Băm nội dung được ký. Nội dung là chuỗi chuẩn hoá do server dựng, không phải do client gửi.</summary>
    public static string Content(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>Vị trí chữ ký đầu tiên làm gãy chuỗi, hoặc null nếu cả chuỗi hợp lệ.</summary>
    public static int? FirstBroken(IReadOnlyList<SignatureRecord> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var previous = Genesis;
        for (var i = 0; i < chain.Count; i++)
        {
            var s = chain[i];
            if (s.PreviousHash != previous ||
                s.Hash != Hash(s.PreviousHash, s.SignatureId, s.SubjectType, s.SubjectId, s.SignerId, s.SignerRole,
                    s.Meaning, s.ContentSha256, s.SignedAt))
            { return i; }
            previous = s.Hash;
        }
        return null;
    }
}

/// <summary>Ai phải ký cho một quyết định chất lượng (scope §6.7 MRB).</summary>
public static class ApprovalPolicy
{
    public const string QualityManager = "QaManager";
    public const string ProductionManager = "ProductionManager";
    public const string QualityEngineer = "QaEngineer";
    public const string CustomerRepresentative = "CustomerRepresentative";

    public static readonly string[] HoldRelease = [QualityManager, ProductionManager];

    public static string[] ForDisposition(string disposition) => disposition switch
    {
        "UseAsIs" => [QualityManager],
        "Rework" => [QualityEngineer],
        "Scrap" => [QualityManager, ProductionManager],
        "Concession" => [QualityManager, CustomerRepresentative],
        _ => throw new ArgumentException($"Unknown disposition {disposition}.", nameof(disposition))
    };

    /// <summary>
    /// Kiểm bộ chữ ký: đủ mỗi vai trò yêu cầu, mỗi người ký một vai trò, không ai là người khởi tạo, mọi chữ ký là
    /// Approved trên đúng nội dung. Trả mã lý do hoặc null.
    /// </summary>
    public static string? Check(IReadOnlyList<SignatureRecord> signatures, IReadOnlyCollection<string> requiredRoles,
        string initiatorId, string subjectType, string subjectId, string contentSha256)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(requiredRoles);
        if (signatures.Any(s => s.SubjectType != subjectType || s.SubjectId != subjectId))
        { return QualityReasonCodes.SignatureWrongSubject; }
        if (signatures.Any(s => s.ContentSha256 != contentSha256))
        { return QualityReasonCodes.SignatureStaleContent; }
        if (signatures.Any(s => s.Meaning != "Approved"))
        { return QualityReasonCodes.SignatureNotApproval; }
        if (signatures.Any(s => string.Equals(s.SignerId, initiatorId, StringComparison.Ordinal)))
        { return QualityReasonCodes.SeparationOfDuties; }
        if (signatures.Select(s => s.SignerId).Distinct(StringComparer.Ordinal).Count() != signatures.Count)
        { return QualityReasonCodes.DuplicateSigner; }
        var missing = requiredRoles.Where(role => !signatures.Any(s => s.SignerRole == role)).ToArray();
        return missing.Length > 0 ? QualityReasonCodes.MissingSignature : null;
    }
}
