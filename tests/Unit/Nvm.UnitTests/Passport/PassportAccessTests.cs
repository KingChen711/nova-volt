using System.Collections.Immutable;
using Nvm.Passport.Commands;
using Nvm.Passport.Entities;

namespace Nvm.UnitTests.Passport;

/// <summary>M12 ★: cùng một passport, năm bên đọc thấy năm tập trường khác nhau; mặc định là từ chối.</summary>
public sealed class PassportAccessTests
{
    private static readonly string[] Identity =
        ["chemistry", "gtin", "manufactureDate", "manufacturer", "manufacturingPlant", "serialNumber"];
    private static readonly string[] Performance = ["expectedLifetimeCycles", "nominalEnergyKwh"];
    private static readonly string[] Sustainability = ["carbonFootprintKgCo2ePerKwh", "recycledContentPercent"];
    private static readonly string[] Composition = ["materialComposition"];
    private static readonly string[] Dismantling = ["dismantlingInstructionsUri", "safetyInstructionsUri"];
    private static readonly string[] Health = ["cycleCount", "negativeEvents", "stateOfHealthPercent"];
    private static readonly string[] Internal = ["genealogy", "recipeVersions", "supplierLots"];

    [Fact]
    public void Public_SeesIdentityPerformanceAndSustainabilityOnly() =>
        Fields(Audiences.Public).ShouldBe(Sorted(Identity, Performance, Sustainability));

    [Fact]
    public void Consumer_Owner_AlsoSeesHealth_ButNotCompositionOrInternal() =>
        Fields(Audiences.Consumer, isOwner: true).ShouldBe(Sorted(Identity, Performance, Sustainability, Health));

    [Fact]
    public void Recycler_SeesEverythingButInternal() =>
        Fields(Audiences.Recycler).ShouldBe(Sorted(Identity, Performance, Sustainability, Composition, Dismantling, Health));

    [Fact]
    public void Repairer_SeesCompositionDismantlingHealth_ButNotSustainability() =>
        Fields(Audiences.Repairer).ShouldBe(Sorted(Identity, Performance, Composition, Dismantling, Health));

    [Fact]
    public void Regulator_SeesEveryClassifiedField_IncludingInternalGenealogy() =>
        Fields(Audiences.Regulator).ShouldBe(Sorted(Identity, Performance, Sustainability, Composition, Dismantling, Health, Internal));

    [Fact]
    public void FiveAudiences_FiveDifferentFieldSets()
    {
        var sets = new[]
        {
            Fields(Audiences.Public), Fields(Audiences.Consumer, isOwner: true), Fields(Audiences.Recycler),
            Fields(Audiences.Repairer), Fields(Audiences.Regulator),
        }.Select(set => string.Join(",", set)).ToArray();
        sets.Distinct(StringComparer.Ordinal).Count().ShouldBe(5);
    }

    [Theory]
    [InlineData(Audiences.Public)]
    [InlineData(Audiences.Consumer)]
    [InlineData(Audiences.Recycler)]
    [InlineData(Audiences.Repairer)]
    public void NobodyButTheRegulator_SeesSupplierLotsRecipesOrGenealogy(string audience)
    {
        var view = PassportProjection.View(Document(), audience, isOwner: true);
        view.Fields.Keys.Intersect(Internal).ShouldBeEmpty();
        string.Join(" ", view.Fields.Values).ShouldNotContain("ROL-NV1-260825-CT1-004");
        string.Join(" ", view.Fields.Values).ShouldNotContain("RCP-COAT");
    }

    [Fact]
    public void Consumer_WhoDoesNotOwnThePack_DoesNotSeeHealth() =>
        Fields(Audiences.Consumer, isOwner: false).ShouldBe(Sorted(Identity, Performance, Sustainability));

    /// <summary>Lab phá hoại M12: thêm trường mà quên gắn AccessClass → không ai thấy (fail closed).</summary>
    [Theory]
    [InlineData(Audiences.Public)]
    [InlineData(Audiences.Consumer)]
    [InlineData(Audiences.Recycler)]
    [InlineData(Audiences.Repairer)]
    [InlineData(Audiences.Regulator)]
    public void FieldWithoutAccessClass_IsDeniedForEveryAudience(string audience)
    {
        var document = Document();
        var leaky = document with { Fields = document.Fields.Add(new PassportField("cellVoltageTraceUri", "s3://raw/curves")) };
        PassportProjection.View(leaky, audience, isOwner: true).Fields.ShouldNotContainKey("cellVoltageTraceUri");
    }

    [Theory]
    [InlineData("auditor")]
    [InlineData("")]
    [InlineData("REGULATOR")]
    public void UnknownAudience_SeesNothing(string audience) =>
        PassportProjection.View(Document(), audience, isOwner: true).Fields.ShouldBeEmpty();

    [Fact]
    public void ContentHash_ChangesWithAnyField_AndIgnoresFieldOrder()
    {
        var document = Document();
        var reordered = document with { Fields = [.. document.Fields.Reverse()] };
        reordered.ContentSha256().ShouldBe(document.ContentSha256());
        var edited = document with { Fields = document.Fields.SetItem(0, document.Fields[0] with { Value = "09506000134369" }) };
        edited.ContentSha256().ShouldNotBe(document.ContentSha256());
    }

    [Theory]
    [InlineData("NV1PP16238A00042", "2026-09-26", "2026-08-26")]
    [InlineData("NV1PP19001A00001", "2026-09-26", "2019-01-01")]   // chữ số năm 9 > 6 → thập kỷ trước
    [InlineData("DE1PP12366A00001", "2032-01-01", "2032-12-31")]   // 2032 là năm nhuận; tham chiếu sớm hơn vẫn cùng năm
    public void ManufactureDate_ComesFromTheSerial(string serial, string reference, string expected)
    {
        var date = PassportProjection.ManufactureDate(serial, DateOnly.Parse(reference, System.Globalization.CultureInfo.InvariantCulture));
        date.ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("NV1PP16366A00001")]   // 2026 không nhuận
    [InlineData("NV1PP16000A00001")]
    public void ManufactureDate_RejectsADayThatTheYearDoesNotHave(string serial) =>
        Should.Throw<ArgumentException>(() => PassportProjection.ManufactureDate(serial, new DateOnly(2026, 12, 31)));

    [Theory]
    [InlineData("09506000134352", true)]
    [InlineData("09506000134353", false)]
    [InlineData("9506000134352", false)]
    [InlineData("0950600013435A", false)]
    public void Gtin14_CheckDigit(string gtin, bool valid) => Gtin14.IsValid(gtin).ShouldBe(valid);

    private static string[] Fields(string audience, bool isOwner = false) =>
        [.. PassportProjection.View(Document(), audience, isOwner).Fields.Keys];

    private static string[] Sorted(params string[][] groups) => [.. groups.SelectMany(g => g).Order(StringComparer.Ordinal)];

    internal static PassportDocument Document() => PassportProjection.Build("NV1",
        new BatteryModel("09506000134352", "NV-P120-NMC", "NovaVolt", "NMC811", 120m, 3000, true,
            """{"cathode":"NMC811","anode":"graphite","electrolyte":"LiPF6"}""", "https://dpp.novavolt.example/docs/p120/dismantling",
            "https://dpp.novavolt.example/docs/p120/safety"),
        new CarbonFootprint("NV-P120-NMC", 2026, 1, 61.5m,
            ImmutableDictionary<string, decimal>.Empty.Add("cobalt", 16m).Add("lithium", 6m).Add("nickel", 6m), "TÜV (giả định)"),
        new PackEvidence("NV1PP16238A00042", "NV-P120-NMC", "Released",
            ["module:NV1MM16238A00001", "cell:NV1CL16238A00001"], ["roll:ROL-NV1-260825-CT1-004"], ["RCP-COAT v2"]),
        new DateOnly(2026, 8, 26), 1, null);
}
