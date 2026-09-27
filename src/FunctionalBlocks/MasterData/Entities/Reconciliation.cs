using System.Security.Cryptography;
using System.Text;
using Nvm.Contracts.Ports;

namespace Nvm.MasterData.Entities;

/// <summary>Một mã chuẩn của MES: vật liệu (có đơn vị gốc), sản phẩm hoặc thiết bị.</summary>
public sealed record CanonicalItem(string Kind, string CanonicalId, string Name, string? BaseUom);

public static class TaskStatuses
{
    public const string Open = "Open";
    public const string Resolved = "Resolved";
}

public static class TaskResolutions
{
    /// <summary>Đóng vì mã đã được ánh xạ.</summary>
    public const string Mapped = "Mapped";

    /// <summary>Sai lệch được chấp nhận cho đúng chứng từ này; không đổi dữ liệu, không quy đổi.</summary>
    public const string Accepted = "Accepted";
}

/// <summary>Việc cần người xử lý do dữ liệu ERP không khớp master data.</summary>
public sealed record ReconciliationTask(string TaskId, ReconciliationIssue Issue, string Status, string? Resolution);

public static class IdentityCode
{
    /// <summary>
    /// Chuẩn hoá tối thiểu và an toàn: bỏ khoảng trắng hai đầu, chữ hoa. Không đoán mã (ví dụ "9812" → "MAT-0009812"):
    /// đoán sai là gán nhầm vật liệu; mã khác dạng phải được ánh xạ tường minh.
    /// </summary>
    public static string Normalize(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.Trim().ToUpperInvariant();
    }

    /// <summary>Id tất định theo sai lệch và chứng từ: cùng file ERP gửi lại không mở task thứ hai.</summary>
    public static string TaskId(ReconciliationIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var key = string.Join('|', issue.Kind, Normalize(issue.ExternalCode), issue.SourceDocument, issue.ReceivedUom ?? "");
        return "RT-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
    }
}
