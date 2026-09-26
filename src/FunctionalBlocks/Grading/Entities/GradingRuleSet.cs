using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nvm.Contracts.Events.Grading;

namespace Nvm.Grading.Entities;

/// <summary>Kết quả áp dụng rule set cho một bộ số đo: đúng một trong bin hoặc mã loại.</summary>
public sealed record GradeOutcome(string? BinCode, string? RejectCode);

/// <summary>Số đo dùng để grade một cell.</summary>
public sealed record GradingMeasurement(decimal CapacityAh, decimal OcvMillivolt, decimal DcirMilliOhm,
    decimal? OcvDriftMillivolt);

/// <summary>
/// Rule set là dữ liệu có version, ngày hiệu lực và người duyệt (scope §6.6). Rule không phải hàm cứng trong code.
/// </summary>
public sealed record GradingRuleSet(string RuleSetId, int Version, string ProductCode, DateTimeOffset EffectiveFrom,
    ImmutableArray<GradingBin> Bins, ImmutableArray<GradingReject> Rejects)
{
    public const string NoBin = "NO_BIN";
    public static readonly string[] Metrics = ["CapacityAh", "OcvMillivolt", "DcirMilliOhm", "OcvDriftMillivolt"];

    /// <summary>Loại trước, rồi bin đầu tiên (theo Priority) chứa cả ba số đo. Không vào bin nào là loại NO_BIN.</summary>
    public GradeOutcome Evaluate(GradingMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        foreach (var reject in Rejects)
        {
            decimal? value = reject.Metric switch
            {
                "CapacityAh" => measurement.CapacityAh,
                "OcvMillivolt" => measurement.OcvMillivolt,
                "DcirMilliOhm" => measurement.DcirMilliOhm,
                "OcvDriftMillivolt" => measurement.OcvDriftMillivolt,
                _ => throw new InvalidDataException($"Unknown grading metric {reject.Metric}.")
            };
            if (value is { } v && (reject.Below ? v < reject.Threshold : v > reject.Threshold))
            { return new GradeOutcome(null, reject.RejectCode); }
        }
        foreach (var bin in Bins.OrderBy(b => b.Priority).ThenBy(b => b.BinCode, StringComparer.Ordinal))
        {
            if (Within(measurement.CapacityAh, bin.CapacityMinAh, bin.CapacityMaxAh) &&
                Within(measurement.OcvMillivolt, bin.OcvMinMillivolt, bin.OcvMaxMillivolt) &&
                Within(measurement.DcirMilliOhm, bin.DcirMinMilliOhm, bin.DcirMaxMilliOhm))
            { return new GradeOutcome(bin.BinCode, null); }
        }
        return new GradeOutcome(null, NoBin);
    }

    /// <summary>Lỗi cấu hình; rỗng nghĩa là rule set hợp lệ để duyệt.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Bins.IsDefaultOrEmpty)
        { problems.Add("Rule set phải có ít nhất một bin."); }
        else
        {
            if (Bins.Select(b => b.BinCode).Distinct(StringComparer.Ordinal).Count() != Bins.Length)
            { problems.Add("Mã bin bị trùng."); }
            if (Bins.Any(b => b.CapacityMaxAh <= b.CapacityMinAh || b.OcvMaxMillivolt <= b.OcvMinMillivolt ||
                    b.DcirMaxMilliOhm <= b.DcirMinMilliOhm))
            { problems.Add("Mỗi khoảng của bin phải có min < max."); }
        }
        if (!Rejects.IsDefault && Rejects.Any(r => !Metrics.Contains(r.Metric, StringComparer.Ordinal)))
        { problems.Add("Tiêu chí loại dùng chỉ số không tồn tại."); }
        return problems;
    }

    /// <summary>Hash nội dung tất định: cùng định nghĩa cho cùng hash, bất kể thứ tự khai báo.</summary>
    public string ContentSha256()
    {
        var text = new StringBuilder().Append(RuleSetId).Append('|').Append(Version).Append('|').Append(ProductCode)
            .Append('|').Append(EffectiveFrom.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        foreach (var bin in Bins.OrderBy(b => b.BinCode, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture,
                $"|B:{bin.BinCode}:{bin.CapacityMinAh}:{bin.CapacityMaxAh}:{bin.OcvMinMillivolt}:{bin.OcvMaxMillivolt}:{bin.DcirMinMilliOhm}:{bin.DcirMaxMilliOhm}:{bin.Priority}");
        }
        foreach (var reject in (Rejects.IsDefault ? [] : Rejects).OrderBy(r => r.RejectCode, StringComparer.Ordinal))
        { text.Append(CultureInfo.InvariantCulture, $"|R:{reject.RejectCode}:{reject.Metric}:{reject.Threshold}:{reject.Below}"); }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static bool Within(decimal value, decimal min, decimal max) => value >= min && value < max;
}
