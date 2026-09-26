using Nvm.Quality.Entities;

namespace Nvm.UnitTests.Quality;

public sealed class QualityDomainTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void XbarR_MatchesHandComputedLimits_AndFlagsTheShiftedSubgroup()
    {
        // Ba subgroup n=4: mean 10, 11, 12; range 2, 2, 2 → X̿ = 11, R̄ = 2, A2 = 0,729, D4 = 2,282, d2 = 2,059.
        SpcSubgroup[] groups =
        [
            new("g1", At, [9m, 10m, 10m, 11m]),
            new("g2", At.AddHours(1), [10m, 11m, 11m, 12m]),
            new("g3", At.AddHours(2), [11m, 12m, 12m, 13m]),
        ];
        var chart = XbarR.Compute(groups, 8m, 14m);
        chart.GrandMean.ShouldBe(11m);
        chart.MeanRange.ShouldBe(2m);
        chart.XbarUcl.ShouldBe(12.458m);
        chart.XbarLcl.ShouldBe(9.542m);
        chart.RangeUcl.ShouldBe(4.564m);
        chart.RangeLcl.ShouldBe(0m);
        chart.Cpk.ShouldBe(Math.Round(3m / (3 * (2m / 2.059m)), 3));
        chart.OutOfControl.ShouldBeEmpty();
        var shifted = XbarR.Compute([.. groups, new SpcSubgroup("g4", At.AddHours(3), [15m, 16m, 16m, 17m])], null, null);
        shifted.OutOfControl.ShouldBe(["g1", "g4"]);   // X̿ = 12,25 nên LCL = 10,79: g1 (mean 10) cũng ngoài giới hạn
        shifted.Cpk.ShouldBeNull();
        Should.Throw<ArgumentException>(() => XbarR.Compute([groups[0], new("bad", At, [1m, 2m])], null, null));
    }

    [Fact]
    public void SignatureChain_BreaksAtTheFirstTamperedByte_AndLaterLinks()
    {
        var chain = new List<SignatureRecord>();
        var previous = SignatureChain.Genesis;
        for (var i = 0; i < 4; i++)
        {
            var content = SignatureChain.Content("payload-" + i);
            var hash = SignatureChain.Hash(previous, "SIG-" + i, "QualityHold", "HOLD-1", "user" + i, "QualityManager",
                "Approved", content, At.AddMinutes(i));
            chain.Add(new SignatureRecord("SIG-" + i, "QualityHold", "HOLD-1", "user" + i, "QualityManager", "Approved",
                content, At.AddMinutes(i), previous, hash));
            previous = hash;
        }
        SignatureChain.FirstBroken(chain).ShouldBeNull();
        var tampered = chain[1] with { ContentSha256 = "0" + chain[1].ContentSha256[1..] };
        SignatureChain.FirstBroken([chain[0], tampered, chain[2], chain[3]]).ShouldBe(1);
        SignatureChain.FirstBroken([chain[0], chain[2], chain[3]]).ShouldBe(1);   // xoá một chữ ký cũng làm gãy chuỗi
    }

    [Fact]
    public void ApprovalPolicy_RequiresEveryRole_DistinctSigners_AndNotTheInitiator()
    {
        var content = SignatureChain.Content("release");
        SignatureRecord Sig(string signer, string role, string meaning = "Approved", string? body = null) =>
            new("S-" + signer + role, "QualityHold", "H1", signer, role, meaning, body ?? content, At, "", "");
        ApprovalPolicy.Check([Sig("qm", "QaManager"), Sig("pm", "ProductionManager")], ApprovalPolicy.HoldRelease,
            "eng", "QualityHold", "H1", content).ShouldBeNull();
        ApprovalPolicy.Check([Sig("qm", "QaManager")], ApprovalPolicy.HoldRelease, "eng", "QualityHold", "H1", content)
            .ShouldBe(QualityReasonCodes.MissingSignature);
        ApprovalPolicy.Check([Sig("eng", "QaManager"), Sig("pm", "ProductionManager")], ApprovalPolicy.HoldRelease,
            "eng", "QualityHold", "H1", content).ShouldBe(QualityReasonCodes.SeparationOfDuties);
        ApprovalPolicy.Check([Sig("qm", "QaManager"), Sig("qm", "ProductionManager")], ApprovalPolicy.HoldRelease,
            "eng", "QualityHold", "H1", content).ShouldBe(QualityReasonCodes.DuplicateSigner);
        ApprovalPolicy.Check([Sig("qm", "QaManager", "Rejected"), Sig("pm", "ProductionManager")],
            ApprovalPolicy.HoldRelease, "eng", "QualityHold", "H1", content).ShouldBe(QualityReasonCodes.SignatureNotApproval);
        ApprovalPolicy.Check([Sig("qm", "QaManager", body: SignatureChain.Content("old")), Sig("pm", "ProductionManager")],
            ApprovalPolicy.HoldRelease, "eng", "QualityHold", "H1", content).ShouldBe(QualityReasonCodes.SignatureStaleContent);
    }
}
