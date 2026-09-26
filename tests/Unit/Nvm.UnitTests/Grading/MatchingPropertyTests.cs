using System.Globalization;
using CsCheck;
using Nvm.Grading.Matching;

namespace Nvm.UnitTests.Grading;

/// <summary>Property test (scope §9/M8): với mọi input hợp lệ, không module nào vi phạm tolerance.</summary>
public sealed class MatchingPropertyTests
{
    private static readonly DateTimeOffset Now = MatchingDataset.Now;

    private static readonly Gen<MatchCell> Cell =
        from index in Gen.Int[1, 99_999]
        from bin in Gen.OneOfConst("A3", "A4")
        from capacity in Gen.Int[119_000, 121_000]
        from ocv in Gen.Int[364_000, 366_000]
        from dcir in Gen.Int[800, 1_000]
        from lot in Gen.Int[1, 5]
        from ageHours in Gen.Int[0, 40 * 24]
        select new MatchCell(string.Create(CultureInfo.InvariantCulture, $"NV1CL16244A{index:D5}"), bin,
            capacity / 1000m, ocv / 100m, dcir / 1000m, "ROLL-" + lot.ToString(CultureInfo.InvariantCulture),
            Now.AddHours(-ageHours));

    private static readonly Gen<(List<MatchCell> Cells, MatchingSpec Spec)> Case =
        from cells in Cell.List[12, 40]
        from spread in Gen.Int[2, 20]
        from lots in Gen.Int[1, 3]
        from reserve in Gen.Int[0, 6]
        select (cells.DistinctBy(c => c.SerialNumber).ToList(), new MatchingSpec
        {
            OcvSpreadMillivolt = spread,
            MaxDistinctLots = lots,
            ReservedPerBin = reserve,
            DcirSpreadMilliOhm = 0.10m,
            CapacitySpreadAh = 0.8m
        });

    [Fact]
    public void TenThousandRandomCases_NeverProduceAModuleOutsideTolerance()
    {
        var greedy = new GreedyMatcher(byOcv: true);
        var cpSat = new CpSatMatcher(windowSize: 48, secondsPerWindow: 0.02, passes: 1, parallelism: 1);
        Case.Sample(input =>
        {
            foreach (var matcher in new IModuleMatcher[] { greedy, cpSat })
            {
                var result = matcher.Match(input.Cells, input.Spec, Now);
                var byId = input.Cells.ToDictionary(c => c.SerialNumber);
                foreach (var module in result.Modules)
                {
                    if (ModuleRules.Violations([.. module.SerialNumbers.Select(s => byId[s])], input.Spec, Now).Count > 0)
                    { return false; }
                }
                var accounted = result.Modules.SelectMany(m => m.SerialNumbers).Concat(result.Leftover)
                    .Concat(result.Reserved).Concat(result.TooOld).ToList();
                if (accounted.Count != input.Cells.Count || accounted.Distinct().Count() != input.Cells.Count)
                { return false; }
            }
            return true;
        }, iter: 10_000);
    }
}
