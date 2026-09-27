using System.Collections.Immutable;
using Nvm.Contracts.Events.Recipe;
using Nvm.Recipe.Entities;

namespace Nvm.UnitTests.Recipe;

public sealed class RecipeVersionTests
{
    private static RecipeVersion Recipe(params RecipeParameter[] parameters) =>
        new("RCP-STACK", 2, "NV-CELL-60AH", "STACK", "STACKER", [.. parameters]);

    private static readonly RecipeParameter Pressure = new("Pressure", 1.2m, 1.0m, 1.5m, "MPa");
    private static readonly RecipeParameter Speed = new("Speed", 30m, 25m, 35m, "ppm");

    [Fact]
    public void SubjectId_CombinesIdAndVersion() => Recipe(Pressure).SubjectId.ShouldBe("RCP-STACK:v2");

    [Fact]
    public void ContentHash_IsKnown_IgnoresParameterOrderAndLifecycle_ButCoversEveryContentField()
    {
        var recipe = Recipe(Pressure, Speed);
        var hash = recipe.ContentSha256();
        hash.ShouldMatch("^[0-9a-f]{64}$");
        Recipe(Speed, Pressure).ContentSha256().ShouldBe(hash);
        (recipe with { Status = RecipeStatus.Active, EffectiveFrom = DateTimeOffset.UnixEpoch }).ContentSha256().ShouldBe(hash);

        RecipeVersion[] changed =
        [
            recipe with { RecipeId = "RCP-X" }, recipe with { Version = 3 }, recipe with { ProductCode = "P2" },
            recipe with { StepCode = "WELD" }, recipe with { EquipmentClass = "WELDER" },
            Recipe(Pressure with { Target = 1.3m }, Speed), Recipe(Pressure with { Min = 0.9m }, Speed),
            Recipe(Pressure with { Max = 1.6m }, Speed), Recipe(Pressure with { UnitOfMeasure = "bar" }, Speed),
            Recipe(Pressure with { Name = "Force" }, Speed), Recipe(Pressure),
        ];
        changed.Select(r => r.ContentSha256()).ShouldAllBe(h => h != hash);
        changed.Select(r => r.ContentSha256()).Distinct().Count().ShouldBe(changed.Length);
    }

    [Fact]
    public void ContentHash_MatchesTheDocumentedCanonicalForm()
    {
        // "RCP-STACK|2|NV-CELL-60AH|STACK|STACKER|Pressure:1.2:1.0:1.5:MPa" băm SHA-256, chữ thường.
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("RCP-STACK|2|NV-CELL-60AH|STACK|STACKER|Pressure:1.2:1.0:1.5:MPa")));
        Recipe(Pressure).ContentSha256().ShouldBe(expected);
    }

    [Fact]
    public void ValidRecipe_HasNoProblems() => Recipe(Pressure, Speed).Problems().ShouldBeEmpty();

    [Fact]
    public void EmptyOrDefaultParameters_AreAProblem()
    {
        Recipe().Problems().ShouldBe(["Recipe phải có ít nhất một tham số."]);
        (Recipe(Pressure) with { Parameters = default }).Problems().ShouldBe(["Recipe phải có ít nhất một tham số."]);
    }

    [Fact]
    public void DuplicateNames_AndOutOfOrderLimits_AreEachReported()
    {
        Recipe(Pressure, Pressure with { Target = 1.1m }).Problems().ShouldBe(["Tên tham số bị trùng."]);
        Recipe(Pressure with { Min = 1.3m }).Problems().ShouldBe(["Mỗi tham số phải có Min ≤ Target ≤ Max."]);
        Recipe(Pressure with { Max = 1.1m }).Problems().ShouldBe(["Mỗi tham số phải có Min ≤ Target ≤ Max."]);
        Recipe(Pressure with { Min = 1.2m, Max = 1.2m }).Problems().ShouldBeEmpty();   // biên: Min = Target = Max
        Recipe(Pressure, Pressure with { Min = 2m }).Problems().Count.ShouldBe(2);
    }

    [Fact]
    public void NewVersion_DefaultsToDraftWithoutEffectivity()
    {
        var recipe = Recipe(Pressure);
        recipe.Status.ShouldBe(RecipeStatus.Draft);
        recipe.EffectiveFrom.ShouldBeNull();
        recipe.EffectiveTo.ShouldBeNull();
        recipe.Parameters.ShouldBe(ImmutableArray.Create(Pressure));
    }
}
