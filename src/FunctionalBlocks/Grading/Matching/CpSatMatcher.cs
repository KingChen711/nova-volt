using System.Collections.Concurrent;
using System.Diagnostics;
using Google.OrTools.Sat;

namespace Nvm.Grading.Matching;

/// <summary>
/// Matching bằng CP-SAT (OR-Tools). Bài toán toàn cục 100k cell quá lớn cho một mô hình, nên chia: trong mỗi
/// bin xếp cell theo lot rồi OCV (tolerance chặt nhất), cắt cửa sổ 48 cell và giải song song; cell dư được xếp
/// lại và giải thêm tối đa hai lượt, lệch cửa sổ để cell ở biên gặp hàng xóm mới. Mỗi cửa sổ bắt đầu từ lời giải
/// heuristic (hint), tối đa hoá số module rồi ưu tiên cell cũ (FIFO). Presolve tắt: cửa sổ nhỏ, thời gian 0,1 s
/// dành cho tìm kiếm; bật presolve thì phần lớn cửa sổ hết giờ trước lời giải đầu tiên (ADR-016).
/// </summary>
public sealed class CpSatMatcher(int windowSize = 48, double secondsPerWindow = 0.1, int passes = 3,
    int? parallelism = null, bool lotFirst = true) : IModuleMatcher
{
    public string Name => "cp-sat";

    /// <summary>Đếm trạng thái solver theo cửa sổ, để lab thấy bao nhiêu cửa sổ hết giờ.</summary>
    public ConcurrentDictionary<string, int> Statuses { get; } = new(StringComparer.Ordinal);

    /// <summary>Tham số CP-SAT bổ sung, dạng <c>,khoá:giá trị</c>.</summary>
    public string ExtraParameters { get; init; } = "";

    public MatchingResult Match(IReadOnlyList<MatchCell> cells, MatchingSpec spec, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(spec);
        var watch = Stopwatch.StartNew();
        var (pool, reserved, tooOld) = ModuleRules.Prepare(cells, spec, now);
        var modules = new ConcurrentBag<(MatchedModule Module, DateTimeOffset Oldest)>();
        var remaining = pool.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        for (var pass = 0; pass < passes; pass++)
        {
            // Lượt sau lệch cửa sổ nửa bước để cell nằm ở biên cửa sổ trước gặp được hàng xóm mới.
            var offset = pass % 2 == 1 ? windowSize / 2 : 0;
            var windows = remaining.SelectMany(bin => Windows(bin.Key, bin.Value, offset)).ToList();
            var used = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var before = modules.Count;
            Parallel.ForEach(windows, new ParallelOptions { MaxDegreeOfParallelism = parallelism ?? Environment.ProcessorCount },
                window =>
                {
                    foreach (var module in Solve(window.Cells, spec, now))
                    {
                        modules.Add((new MatchedModule(window.Bin, [.. module.Select(c => c.SerialNumber)]),
                            module.Min(c => c.GradedAt)));
                        foreach (var cell in module)
                        { used[cell.SerialNumber] = 0; }
                    }
                });
            remaining = remaining.ToDictionary(p => p.Key,
                p => p.Value.Where(c => !used.ContainsKey(c.SerialNumber)).ToList(), StringComparer.Ordinal);
            if (modules.Count == before)
            { break; }
        }
        var chosen = modules.OrderBy(m => m.Oldest).ThenBy(m => m.Module.SerialNumbers[0], StringComparer.Ordinal)
            .Select(m => m.Module).ToList();
        var leftover = remaining.Values.SelectMany(c => c).Select(c => c.SerialNumber).ToList();
        if (spec.MaxModules is { } cap && chosen.Count > cap)
        {
            leftover.AddRange(chosen.Skip(cap).SelectMany(m => m.SerialNumbers));
            chosen = chosen.Take(cap).ToList();
        }
        return new MatchingResult(Name, [.. chosen.OrderBy(m => m.BinCode, StringComparer.Ordinal)], [.. leftover],
            [.. reserved], [.. tooOld], watch.Elapsed);
    }

    private IEnumerable<(string Bin, List<MatchCell> Cells)> Windows(string bin, List<MatchCell> cells, int offset)
    {
        var ordered = (lotFirst
                ? cells.OrderBy(c => c.LotId, StringComparer.Ordinal).ThenBy(c => c.OcvMillivolt)
                : cells.OrderBy(c => c.OcvMillivolt))
            .ThenBy(c => c.SerialNumber, StringComparer.Ordinal).ToList();
        var start = 0;
        if (offset > 0 && ordered.Count > offset)
        {
            yield return (bin, ordered.GetRange(0, offset));
            start = offset;
        }
        for (; start < ordered.Count; start += windowSize)
        { yield return (bin, ordered.GetRange(start, Math.Min(windowSize, ordered.Count - start))); }
    }

    /// <summary>Một cửa sổ: tối đa hoá số module đạt mọi tolerance, rồi tuổi cell được dùng.</summary>
    internal List<List<MatchCell>> Solve(List<MatchCell> cells, MatchingSpec spec, DateTimeOffset now)
    {
        var size = spec.CellsPerModule;
        var slots = cells.Count / size;
        if (slots == 0)
        { return []; }
        var model = new CpModel();
        var x = new BoolVar[cells.Count, slots];
        var y = new BoolVar[slots];
        for (var k = 0; k < slots; k++)
        { y[k] = model.NewBoolVar($"y{k}"); }
        for (var i = 0; i < cells.Count; i++)
        {
            for (var k = 0; k < slots; k++)
            { x[i, k] = model.NewBoolVar($"x{i}_{k}"); }
            model.AddAtMostOne(Enumerable.Range(0, slots).Select(k => (ILiteral)x[i, k]));
        }
        var capacity = cells.Select(c => (long)Math.Round(c.CapacityAh * 1000m)).ToArray();
        var ocv = cells.Select(c => (long)Math.Round(c.OcvMillivolt * 100m)).ToArray();
        var dcir = cells.Select(c => (long)Math.Round(c.DcirMilliOhm * 1000m)).ToArray();
        var lots = cells.Select(c => c.LotId).Distinct(StringComparer.Ordinal).ToArray();
        var lotIndex = lots.Select((lot, index) => (lot, index)).ToDictionary(p => p.lot, p => p.index, StringComparer.Ordinal);
        for (var k = 0; k < slots; k++)
        {
            model.Add(LinearExpr.Sum(Enumerable.Range(0, cells.Count).Select(i => x[i, k])) == size * (LinearExpr)y[k]);
            if (k > 0)
            { model.Add(y[k - 1] >= y[k]); }
            Spread(model, x, k, capacity, (long)Math.Round(spec.CapacitySpreadAh * 1000m));
            Spread(model, x, k, ocv, (long)Math.Round(spec.OcvSpreadMillivolt * 100m));
            Spread(model, x, k, dcir, (long)Math.Round(spec.DcirSpreadMilliOhm * 1000m));
            if (lots.Length > spec.MaxDistinctLots)
            {
                var z = lots.Select(lot => model.NewBoolVar($"z{k}_{lot}")).ToArray();
                for (var i = 0; i < cells.Count; i++)
                { model.AddImplication(x[i, k], z[lotIndex[cells[i].LotId]]); }
                model.Add(LinearExpr.Sum(z) <= spec.MaxDistinctLots);
            }
        }
        // FIFO: tuổi tính theo giờ, giới hạn 720; một module luôn đáng giá hơn mọi chênh lệch tuổi trong cửa sổ.
        var age = cells.Select(c => (long)Math.Clamp((now - c.GradedAt).TotalHours, 0, 720)).ToArray();
        var moduleWeight = cells.Count * 721L + 1;
        var objective = LinearExpr.WeightedSum(y, Enumerable.Repeat(moduleWeight, slots));
        var terms = new List<LinearExpr> { objective };
        for (var i = 0; i < cells.Count; i++)
        {
            if (age[i] == 0)
            { continue; }
            terms.Add(LinearExpr.WeightedSum(Enumerable.Range(0, slots).Select(k => x[i, k]),
                Enumerable.Repeat(age[i], slots)));
        }
        model.Maximize(LinearExpr.Sum(terms));
        // Gợi ý từ heuristic cắt khúc theo OCV: solver bắt đầu từ một lời giải hợp lệ và chỉ phải cải thiện nó.
        var hint = HintModules(cells, spec, now);
        for (var k = 0; k < slots; k++)
        {
            model.AddHint(y[k], k < hint.Count ? 1 : 0);
            for (var i = 0; i < cells.Count; i++)
            { model.AddHint(x[i, k], k < hint.Count && hint[k].Contains(i) ? 1 : 0); }
        }
        var solver = new CpSolver
        {
            StringParameters = FormattableString.Invariant(
                $"num_workers:1,max_time_in_seconds:{secondsPerWindow},random_seed:7,log_search_progress:false,cp_model_presolve:false{ExtraParameters}")
        };
        var status = solver.Solve(model);
        Statuses.AddOrUpdate(status.ToString(), 1, (_, count) => count + 1);
        if (status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible))
        {
            // Hết giờ trước khi có lời giải: dùng gợi ý (đã hợp lệ) thay vì bỏ cả cửa sổ.
            return [.. hint.Select(module => module.Select(i => cells[i]).ToList())];
        }
        var result = new List<List<MatchCell>>();
        for (var k = 0; k < slots; k++)
        {
            if (!solver.BooleanValue(y[k]))
            { continue; }
            List<MatchCell> module = [.. Enumerable.Range(0, cells.Count).Where(i => solver.BooleanValue(x[i, k])).Select(i => cells[i])];
            // Phòng thủ: module lệch spec (ví dụ do làm tròn số nguyên) không bao giờ rời khỏi matcher.
            if (ModuleRules.Violations(module, spec, now).Count == 0)
            { result.Add(module); }
        }
        return result;
    }

    private static List<HashSet<int>> HintModules(List<MatchCell> cells, MatchingSpec spec, DateTimeOffset now)
    {
        var order = Enumerable.Range(0, cells.Count).OrderBy(i => cells[i].OcvMillivolt).ThenBy(i => cells[i].DcirMilliOhm).ToList();
        var modules = new List<HashSet<int>>();
        var start = 0;
        while (start + spec.CellsPerModule <= order.Count)
        {
            var chunk = order.GetRange(start, spec.CellsPerModule);
            if (ModuleRules.Violations([.. chunk.Select(i => cells[i])], spec, now).Count == 0)
            {
                modules.Add([.. chunk]);
                start += spec.CellsPerModule;
            }
            else
            { start++; }
        }
        return modules;
    }

    private static void Spread(CpModel model, BoolVar[,] x, int k, long[] values, long tolerance)
    {
        var lo = model.NewIntVar(values.Min(), values.Max(), $"lo{k}");
        var hi = model.NewIntVar(values.Min(), values.Max(), $"hi{k}");
        for (var i = 0; i < values.Length; i++)
        {
            model.Add(lo <= values[i]).OnlyEnforceIf(x[i, k]);
            model.Add(hi >= values[i]).OnlyEnforceIf(x[i, k]);
        }
        model.Add(hi - lo <= tolerance);
    }
}
