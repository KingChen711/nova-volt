using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Identity;

/// <summary>Step mà máy thực hiện, đọc từ code của nó.</summary>
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
        // Formation channel có tên FORM-01-CH-0142 nên tình cờ cho cùng kết quả. Vị trí trên aging
        // rack thì không, và đó là case mà rule này được viết ra để xử lý.
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
        // Convention làm nền cho rule này là property của document, không phải code — nên nó được
        // kiểm với document. Work cell thêm sau không theo convention sẽ làm test đỏ; đó là lúc quyết
        // định naming hay rule mới sai.
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
