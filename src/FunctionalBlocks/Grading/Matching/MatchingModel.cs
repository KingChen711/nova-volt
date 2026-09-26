using System.Collections.Immutable;

namespace Nvm.Grading.Matching;

/// <summary>
/// Một cell đã grade, còn trong kho, ứng viên ghép module. <c>LotId</c> là lot điện cực (cuộn cathode) dùng cho
/// ràng buộc số lot mỗi module; <c>GradedAt</c> dùng cho FIFO (ưu tiên cell cũ).
/// </summary>
public sealed record MatchCell(string SerialNumber, string BinCode, decimal CapacityAh, decimal OcvMillivolt,
    decimal DcirMilliOhm, string LotId, DateTimeOffset GradedAt);

/// <summary>Ràng buộc ghép module (scope §6.6). Giá trị mặc định là của sản phẩm A.</summary>
public sealed record MatchingSpec
{
    public int CellsPerModule { get; init; } = 12;
    public decimal CapacitySpreadAh { get; init; } = 0.8m;
    public decimal OcvSpreadMillivolt { get; init; } = 10m;
    public decimal DcirSpreadMilliOhm { get; init; } = 0.15m;
    public int MaxDistinctLots { get; init; } = 3;

    /// <summary>Cell tồn kho lâu hơn giới hạn này bị loại khỏi matching (phải qua MRB).</summary>
    public TimeSpan MaxCellAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Số cell mỗi bin giữ lại cho đơn ưu tiên; không được dùng trong lượt này.</summary>
    public int ReservedPerBin { get; init; }

    /// <summary>Trần số module của lượt chạy (phần site được phân bổ); null là không giới hạn.</summary>
    public int? MaxModules { get; init; }
}

public sealed record MatchedModule(string BinCode, ImmutableArray<string> SerialNumbers);

/// <summary>
/// Kết quả một lượt matching. <c>Leftover</c>: cell hợp lệ nhưng không ghép được nhóm; <c>Reserved</c>: cell
/// giữ lại cho đơn ưu tiên; <c>TooOld</c>: cell quá tuổi tồn kho, bị loại trước khi ghép.
/// </summary>
public sealed record MatchingResult(string Algorithm, ImmutableArray<MatchedModule> Modules,
    ImmutableArray<string> Leftover, ImmutableArray<string> Reserved, ImmutableArray<string> TooOld, TimeSpan Runtime);

public interface IModuleMatcher
{
    string Name { get; }

    MatchingResult Match(IReadOnlyList<MatchCell> cells, MatchingSpec spec, DateTimeOffset now);
}

/// <summary>Kiểm một module theo spec; dùng trong mọi matcher và trong property test.</summary>
public static class ModuleRules
{
    public static IReadOnlyList<string> Violations(IReadOnlyList<MatchCell> module, MatchingSpec spec, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(spec);
        var problems = new List<string>();
        if (module.Count != spec.CellsPerModule)
        { problems.Add("CELL_COUNT"); }
        if (module.Count == 0)
        { return problems; }
        if (module.Select(c => c.BinCode).Distinct(StringComparer.Ordinal).Count() > 1)
        { problems.Add("MIXED_BIN"); }
        if (module.Max(c => c.CapacityAh) - module.Min(c => c.CapacityAh) > spec.CapacitySpreadAh)
        { problems.Add("CAPACITY_SPREAD"); }
        if (module.Max(c => c.OcvMillivolt) - module.Min(c => c.OcvMillivolt) > spec.OcvSpreadMillivolt)
        { problems.Add("OCV_SPREAD"); }
        if (module.Max(c => c.DcirMilliOhm) - module.Min(c => c.DcirMilliOhm) > spec.DcirSpreadMilliOhm)
        { problems.Add("DCIR_SPREAD"); }
        if (module.Select(c => c.LotId).Distinct(StringComparer.Ordinal).Count() > spec.MaxDistinctLots)
        { problems.Add("TOO_MANY_LOTS"); }
        if (module.Any(c => now - c.GradedAt > spec.MaxCellAge))
        { problems.Add("CELL_TOO_OLD"); }
        if (module.Select(c => c.SerialNumber).Distinct(StringComparer.Ordinal).Count() != module.Count)
        { problems.Add("DUPLICATE_CELL"); }
        return problems;
    }

    /// <summary>Tách cell quá tuổi và cell dự phòng (cell mới nhất mỗi bin), trả phần được đem ghép theo bin.</summary>
    internal static (Dictionary<string, List<MatchCell>> Pool, List<string> Reserved, List<string> TooOld) Prepare(
        IReadOnlyList<MatchCell> cells, MatchingSpec spec, DateTimeOffset now)
    {
        var tooOld = new List<string>();
        var reserved = new List<string>();
        var pool = new Dictionary<string, List<MatchCell>>(StringComparer.Ordinal);
        foreach (var bin in cells.GroupBy(c => c.BinCode, StringComparer.Ordinal))
        {
            var fresh = new List<MatchCell>();
            foreach (var cell in bin)
            {
                if (now - cell.GradedAt > spec.MaxCellAge)
                { tooOld.Add(cell.SerialNumber); }
                else
                { fresh.Add(cell); }
            }
            // Dự phòng lấy cell mới nhất: cell cũ phải được dùng trước (FIFO).
            var ordered = fresh.OrderByDescending(c => c.GradedAt).ThenBy(c => c.SerialNumber, StringComparer.Ordinal).ToList();
            var keep = Math.Min(spec.ReservedPerBin, ordered.Count);
            reserved.AddRange(ordered.Take(keep).Select(c => c.SerialNumber));
            pool[bin.Key] = ordered.Skip(keep).ToList();
        }
        return (pool, reserved, tooOld);
    }
}
