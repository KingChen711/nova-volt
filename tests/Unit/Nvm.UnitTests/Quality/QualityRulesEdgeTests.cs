using System.Security.Cryptography;
using System.Text;
using Nvm.Quality.Entities;

namespace Nvm.UnitTests.Quality;

/// <summary>Biên của Cpk một phía, định dạng hash chữ ký và bảng người ký theo disposition.</summary>
public sealed class QualityRulesEdgeTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    // X̿ = 11, R̄ = 2, n = 4 → σ = 2 / 2,059.
    private static readonly SpcSubgroup[] Groups =
    [
        new("g1", At, [9m, 10m, 10m, 11m]),
        new("g2", At.AddHours(1), [10m, 11m, 11m, 12m]),
        new("g3", At.AddHours(2), [11m, 12m, 12m, 13m]),
    ];

    private static decimal Side(decimal distance) => Math.Round(distance / (3 * (2m / 2.059m)), 3);

    [Fact]
    public void Cpk_IsTheWorseSide_AndWorksWithOnlyOneSpecLimit()
    {
        XbarR.Compute(Groups, 8m, 16m).Cpk.ShouldBe(Side(3m));      // gần LSL hơn
        XbarR.Compute(Groups, 6m, 14m).Cpk.ShouldBe(Side(3m));      // gần USL hơn
        XbarR.Compute(Groups, null, 14m).Cpk.ShouldBe(Side(3m));
        XbarR.Compute(Groups, 9m, null).Cpk.ShouldBe(Side(2m));
        XbarR.Compute(Groups, null, 12m).Cpk.ShouldBe(Side(1m));
    }

    [Fact]
    public void Cpk_IsUndefined_WithoutVariation()
    {
        SpcSubgroup[] flat = [new("f1", At, [5m, 5m]), new("f2", At, [5m, 5m])];
        XbarR.Compute(flat, 4m, 6m).Cpk.ShouldBeNull();
    }

    [Fact]
    public void Meanings_AreTheFourScopeMeanings() =>
        SignatureChain.Meanings.ShouldBe(["Approved", "Rejected", "Reviewed", "Witnessed"]);

    [Fact]
    public void Hash_UsesUnitSeparatorAndUtcRoundTripTime()
    {
        var local = new DateTimeOffset(2026, 9, 20, 15, 0, 0, TimeSpan.FromHours(7));   // = 08:00 UTC
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f',
            SignatureChain.Genesis, "SIG-1", "QualityHold", "H1", "qm", "QaManager", "Approved", "abc",
            "2026-09-20T08:00:00.0000000+00:00"))));
        SignatureChain.Hash(SignatureChain.Genesis, "SIG-1", "QualityHold", "H1", "qm", "QaManager", "Approved", "abc", local)
            .ShouldBe(expected);
        SignatureChain.Content("abc").ShouldBe(Convert.ToHexStringLower(SHA256.HashData("abc"u8.ToArray())));
        SignatureChain.Genesis.ShouldBe(new string('0', 64));
    }

    [Fact]
    public void FirstBroken_RejectsNull_AndDetectsAWrongPreviousHash()
    {
        Should.Throw<ArgumentNullException>(() => SignatureChain.FirstBroken(null!));
        SignatureChain.FirstBroken([]).ShouldBeNull();
        var hash = SignatureChain.Hash("f00d", "S", "T", "I", "u", "r", "Approved", "c", At);
        SignatureChain.FirstBroken([new SignatureRecord("S", "T", "I", "u", "r", "Approved", "c", At, "f00d", hash)]).ShouldBe(0);
    }

    [Theory]
    [InlineData("UseAsIs", new[] { "QaManager" })]
    [InlineData("Rework", new[] { "QaEngineer" })]
    [InlineData("Scrap", new[] { "QaManager", "ProductionManager" })]
    [InlineData("Concession", new[] { "QaManager", "CustomerRepresentative" })]
    public void ForDisposition_NamesTheRequiredSigners(string disposition, string[] roles) =>
        ApprovalPolicy.ForDisposition(disposition).ShouldBe(roles);

    [Fact]
    public void ForDisposition_RejectsUnknownValues()
    {
        var error = Should.Throw<ArgumentException>(() => ApprovalPolicy.ForDisposition("Donate"));
        error.ParamName.ShouldBe("disposition");
        error.Message.ShouldStartWith("Unknown disposition Donate.");
        ApprovalPolicy.HoldRelease.ShouldBe(["QaManager", "ProductionManager"]);
    }

    [Fact]
    public void Check_RejectsSignaturesForAnotherSubject_EvenIfOnlyOneFieldDiffers()
    {
        var content = SignatureChain.Content("release");
        SignatureRecord Sig(string signer, string role, string type = "QualityHold", string id = "H1") =>
            new("S-" + signer, type, id, signer, role, "Approved", content, At, "", "");
        ApprovalPolicy.Check([Sig("qm", "QaManager", id: "H2"), Sig("pm", "ProductionManager")], ApprovalPolicy.HoldRelease,
            "eng", "QualityHold", "H1", content).ShouldBe(QualityReasonCodes.SignatureWrongSubject);
        ApprovalPolicy.Check([Sig("qm", "QaManager"), Sig("pm", "ProductionManager", type: "NonConformance")],
            ApprovalPolicy.HoldRelease, "eng", "QualityHold", "H1", content).ShouldBe(QualityReasonCodes.SignatureWrongSubject);
        // Đủ số người nhưng thiếu đúng một vai trò.
        ApprovalPolicy.Check([Sig("qm", "QaManager"), Sig("qe", "QaEngineer")], ApprovalPolicy.HoldRelease,
            "eng", "QualityHold", "H1", content).ShouldBe(QualityReasonCodes.MissingSignature);
        Should.Throw<ArgumentNullException>(() => ApprovalPolicy.Check(null!, ApprovalPolicy.HoldRelease, "e", "T", "I", content));
        Should.Throw<ArgumentNullException>(() => ApprovalPolicy.Check([], null!, "e", "T", "I", content));
    }
}
