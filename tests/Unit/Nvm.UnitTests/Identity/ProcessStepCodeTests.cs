using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Identity;

/// <summary>The step a machine performs, read off its code.</summary>
public sealed class ProcessStepCodeTests
{
    [Theory]
    [InlineData("NOVAVOLT/NV1/FORMATION/F1/FORM-01", "FORM")]
    [InlineData("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142", "FORM")]
    [InlineData("NOVAVOLT/NV1/ASSEMBLY/L1/STACK-01", "STACK")]
    [InlineData("NOVAVOLT/NV1/ELECTRODE/E1/COAT-01", "COAT")]
    [InlineData("NOVAVOLT/NV1/AGING/A1/AGE-RACK-01", "AGE")]
    [InlineData("NOVAVOLT/DE1/PACK/P1/PLOAD-01", "PLOAD")]
    public void TheStepComesFromTheWorkCellCode(string path, string expected) =>
        ProcessStepCode.FromEquipmentPath(EquipmentPath.Parse(path)).ShouldBe(expected);

    [Fact]
    public void TheStepComesFromTheWorkCellAndNotFromTheDeepestSegment()
    {
        // A formation channel is called FORM-01-CH-0142 and would give the same answer by accident.
        // An aging rack position would not, and that is the case this rule is written for.
        ProcessStepCode
            .FromEquipmentPath(EquipmentPath.Parse("NOVAVOLT/NV1/AGING/A1/AGE-RACK-01/SLOT-07"))
            .ShouldBe("AGE");
    }

    [Theory]
    [InlineData("NOVAVOLT")]
    [InlineData("NOVAVOLT/NV1")]
    [InlineData("NOVAVOLT/NV1/FORMATION")]
    [InlineData("NOVAVOLT/NV1/FORMATION/F1")]
    public void NothingShallowerThanAWorkCellPerformsAStep(string path) =>
        ProcessStepCode.FromEquipmentPath(EquipmentPath.Parse(path)).ShouldBeNull();

    [Fact]
    public void EveryWorkCellInTheSeedYieldsAStep()
    {
        // The convention this rule rests on is a property of the documents, not of the code — so it is
        // checked against the documents. A work cell added later that does not follow it makes this
        // red, which is the moment to decide whether the naming or the rule is wrong.
        var catalog = FactoryModelSeed.LoadCatalog(Path.Combine(AppContext.BaseDirectory, "seed"));

        var workCells = catalog.Revisions
            .SelectMany(revision => catalog.Find(revision)!.Paths)
            .Where(path => path.Kind == FactoryNodeKind.WorkCell)
            .Distinct()
            .ToList();

        workCells.ShouldNotBeEmpty();

        foreach (var workCell in workCells)
        {
            ProcessStepCode.FromEquipmentPath(workCell)
                .ShouldNotBeNullOrWhiteSpace($"{workCell.Value} yields no step code");
        }
    }
}
