using Nvm.Contracts.Ports;
using Nvm.MasterData.Entities;

namespace Nvm.UnitTests.MasterData;

public sealed class IdentityCodeTests
{
    private static ReconciliationIssue Issue(string code = "mat-0009812", string? uom = "kg", string document = "PO-1") =>
        new("UnknownMaterial", code, null, uom, null, document, "detail");

    [Fact]
    public void Normalize_TrimsAndUppercases_ButNeverGuesses()
    {
        IdentityCode.Normalize("  mat-0009812\t").ShouldBe("MAT-0009812");
        IdentityCode.Normalize("9812").ShouldBe("9812");
        Should.Throw<ArgumentNullException>(() => IdentityCode.Normalize(null!));
    }

    [Fact]
    public void TaskId_IsDeterministic_AndIgnoresCaseOfTheExternalCode()
    {
        var id = IdentityCode.TaskId(Issue());
        id.ShouldMatch("^RT-[0-9A-F]{12}$");
        IdentityCode.TaskId(Issue(code: " MAT-0009812 ")).ShouldBe(id);
        var key = "UnknownMaterial|MAT-0009812|PO-1|kg";
        id.ShouldBe("RT-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)))[..12]);
    }

    [Fact]
    public void TaskId_DiffersPerKindCodeDocumentAndUom()
    {
        var id = IdentityCode.TaskId(Issue());
        string[] others =
        [
            IdentityCode.TaskId(Issue() with { Kind = "UomMismatch" }),
            IdentityCode.TaskId(Issue(code: "MAT-1")),
            IdentityCode.TaskId(Issue(document: "PO-2")),
            IdentityCode.TaskId(Issue(uom: "g")),
            IdentityCode.TaskId(Issue(uom: null)),
        ];
        others.ShouldAllBe(other => other != id);
        others.Distinct().Count().ShouldBe(others.Length);
        IdentityCode.TaskId(Issue() with { Detail = "khác" }).ShouldBe(id);   // mô tả không thuộc khoá
        Should.Throw<ArgumentNullException>(() => IdentityCode.TaskId(null!));
    }
}
