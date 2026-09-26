using System.Collections.Immutable;
using System.Diagnostics;

namespace Nvm.Grading.Matching;

/// <summary>
/// Baseline (scope §6.6): trong mỗi bin, xếp theo lot rồi capacity và cắt khúc 12 liên tiếp. Khúc không hợp lệ
/// thì bỏ cell đầu khúc và thử lại từ cell kế tiếp. Biến thể <c>byOcv</c> xếp theo lot rồi OCV — một heuristic
/// mạnh hơn, dùng để so sánh công bằng với CP-SAT (ADR-016).
/// </summary>
public sealed class GreedyMatcher(bool byOcv = false) : IModuleMatcher
{
    public string Name => byOcv ? "greedy-ocv" : "greedy";

    public MatchingResult Match(IReadOnlyList<MatchCell> cells, MatchingSpec spec, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(spec);
        var watch = Stopwatch.StartNew();
        var (pool, reserved, tooOld) = ModuleRules.Prepare(cells, spec, now);
        var modules = ImmutableArray.CreateBuilder<MatchedModule>();
        var leftover = new List<string>();
        foreach (var (bin, binCells) in pool.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var ordered = binCells.OrderBy(c => c.LotId, StringComparer.Ordinal)
                .ThenBy(c => byOcv ? c.OcvMillivolt : c.CapacityAh)
                .ThenBy(c => c.SerialNumber, StringComparer.Ordinal).ToList();
            var start = 0;
            while (start + spec.CellsPerModule <= ordered.Count)
            {
                if (spec.MaxModules is { } cap && modules.Count >= cap)
                { break; }
                var chunk = ordered.GetRange(start, spec.CellsPerModule);
                if (ModuleRules.Violations(chunk, spec, now).Count == 0)
                {
                    modules.Add(new MatchedModule(bin, [.. chunk.Select(c => c.SerialNumber)]));
                    start += spec.CellsPerModule;
                }
                else
                {
                    leftover.Add(ordered[start].SerialNumber);
                    start++;
                }
            }
            leftover.AddRange(ordered.Skip(start).Select(c => c.SerialNumber));
        }
        return new MatchingResult(Name, modules.ToImmutable(), [.. leftover], [.. reserved], [.. tooOld], watch.Elapsed);
    }
}
