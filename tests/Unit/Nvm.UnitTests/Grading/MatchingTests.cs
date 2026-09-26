using System.Globalization;
using Nvm.Grading.Matching;

namespace Nvm.UnitTests.Grading;

public sealed class MatchingTests(ITestOutputHelper output)
{
    [Fact]
    public void BothMatchers_ProduceOnlyValidModules_AndUseEachCellOnce()
    {
        var cells = MatchingDataset.Create(3_000);
        var spec = new MatchingSpec { ReservedPerBin = 12 };
        foreach (var matcher in new IModuleMatcher[] { new GreedyMatcher(), new CpSatMatcher(secondsPerWindow: 0.2) })
        {
            var result = matcher.Match(cells, spec, MatchingDataset.Now);
            var byId = cells.ToDictionary(c => c.SerialNumber);
            foreach (var module in result.Modules)
            {
                ModuleRules.Violations([.. module.SerialNumbers.Select(s => byId[s])], spec, MatchingDataset.Now)
                    .ShouldBeEmpty(matcher.Name);
            }
            var accounted = result.Modules.SelectMany(m => m.SerialNumbers).Concat(result.Leftover)
                .Concat(result.Reserved).Concat(result.TooOld).ToList();
            accounted.Count.ShouldBe(cells.Count, matcher.Name);
            accounted.Distinct().Count().ShouldBe(cells.Count, matcher.Name);
            output.WriteLine($"{matcher.Name} modules={result.Modules.Length} ms={result.Runtime.TotalMilliseconds:F0}");
        }
    }

    [Fact]
    public void ReserveKeepsNewestCells_AndOldCellsAreExcluded()
    {
        var now = MatchingDataset.Now;
        var cells = Enumerable.Range(1, 30).Select(i => new MatchCell(
            string.Create(CultureInfo.InvariantCulture, $"NV1CL16244A{i:D5}"), "A4", 120m, 3650m, 0.9m, "ROLL-1",
            now.AddDays(-i))).ToList();
        var result = new GreedyMatcher().Match(cells, new MatchingSpec { ReservedPerBin = 6 }, now);
        result.TooOld.ShouldBeEmpty();
        result.Reserved.ShouldBe(cells.Take(6).Select(c => c.SerialNumber));
        result.Modules.Length.ShouldBe(2);
        var old = cells.Append(cells[0] with { SerialNumber = "NV1CL16244A09999", GradedAt = now.AddDays(-31) }).ToList();
        new GreedyMatcher().Match(old, new MatchingSpec(), now).TooOld.ShouldBe(["NV1CL16244A09999"]);
    }

    /// <summary>Lab M8 (N10/T6): 100.000 cell, greedy so với CP-SAT. Số đo ghi vào ADR-016.</summary>
    [Fact]
    public void HundredThousandCells_CpSatBeatsGreedyByEightPercent_InUnderThirtySeconds()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M8 matching lab (ADR-016).");
        var size = int.Parse(Environment.GetEnvironmentVariable("NVM_MATCHING_CELLS") ?? "100000", CultureInfo.InvariantCulture);
        var cells = MatchingDataset.Create(size);
        var spec = new MatchingSpec();
        var greedy = new GreedyMatcher().Match(cells, spec, MatchingDataset.Now);
        var matcher = new CpSatMatcher(
            int.Parse(Environment.GetEnvironmentVariable("NVM_CPSAT_WINDOW") ?? "48", CultureInfo.InvariantCulture),
            double.Parse(Environment.GetEnvironmentVariable("NVM_CPSAT_SECONDS") ?? "0.1", CultureInfo.InvariantCulture),
            lotFirst: Environment.GetEnvironmentVariable("NVM_CPSAT_LOTFIRST") != "0")
        { ExtraParameters = Environment.GetEnvironmentVariable("NVM_CPSAT_EXTRA") ?? "" };
        var cpSat = matcher.Match(cells, spec, MatchingDataset.Now);
        output.WriteLine("MATCHING statuses " + string.Join(",", matcher.Statuses.Select(p => p.Key + "=" + p.Value)));
        var greedyOcv = new GreedyMatcher(byOcv: true).Match(cells, spec, MatchingDataset.Now);
        foreach (var result in new[] { greedy, greedyOcv, cpSat })
        {
            var used = result.Modules.SelectMany(m => m.SerialNumbers).ToHashSet();
            var averageAge = cells.Where(c => used.Contains(c.SerialNumber))
                .Average(c => (MatchingDataset.Now - c.GradedAt).TotalDays);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"MATCHING algorithm={result.Algorithm} cells={cells.Count} modules={result.Modules.Length} " +
                $"leftover_pct={100.0 * result.Leftover.Length / cells.Count:F2} avg_age_days={averageAge:F2} " +
                $"runtime_s={result.Runtime.TotalSeconds:F2}"));
        }
        var gain = (double)cpSat.Modules.Length / greedy.Modules.Length - 1;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"MATCHING gain_pct={gain * 100:F2} gain_over_greedy_ocv_pct={((double)cpSat.Modules.Length / greedyOcv.Modules.Length - 1) * 100:F2}"));
        gain.ShouldBeGreaterThanOrEqualTo(0.08, "T6: CP-SAT must build at least 8% more modules than greedy");
        cpSat.Runtime.ShouldBeLessThan(TimeSpan.FromSeconds(30), "N10");
    }

    /// <summary>Lab M8: bỏ MaxDistinctLots thì một lot lan vào nhiều module hơn — phạm vi recall rộng hơn.</summary>
    [Fact]
    public void WithoutMaxDistinctLots_ALotSpreadsIntoMoreModules()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M8 lot-mixing lab (ADR-016).");
        var cells = MatchingDataset.Create(20_000);
        var byId = cells.ToDictionary(c => c.SerialNumber);
        foreach (var lots in new[] { 3, int.MaxValue })
        {
            var result = new CpSatMatcher().Match(cells, new MatchingSpec { MaxDistinctLots = lots }, MatchingDataset.Now);
            var lotsPerModule = result.Modules.Select(m => m.SerialNumbers.Select(s => byId[s].LotId).Distinct().Count()).ToList();
            var affected = cells.Select(c => c.LotId).Distinct()
                .Select(lot => result.Modules.Count(m => m.SerialNumbers.Any(s => byId[s].LotId == lot))).ToList();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"LOT_MIX max_lots={(lots == int.MaxValue ? "none" : lots.ToString(CultureInfo.InvariantCulture))} " +
                $"modules={result.Modules.Length} avg_lots_per_module={lotsPerModule.Average():F2} max_lots_per_module={lotsPerModule.Max()} " +
                $"modules_per_lot_avg={affected.Average():F1} modules_per_lot_max={affected.Max()}"));
        }
    }
}
