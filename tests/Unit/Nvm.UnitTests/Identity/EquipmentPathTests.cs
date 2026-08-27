using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Identity;

public sealed class EquipmentPathTests
{
    // The worked example from docs/scope.md §2.1: charging channel 142 on formation machine 01.
    private const string Channel = "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142";

    [Theory]
    [InlineData("NOVAVOLT", FactoryNodeKind.Enterprise)]
    [InlineData("NOVAVOLT/NV1", FactoryNodeKind.Site)]
    [InlineData("NOVAVOLT/NV1/FORMATION", FactoryNodeKind.Area)]
    [InlineData("NOVAVOLT/NV1/FORMATION/F1", FactoryNodeKind.Line)]
    [InlineData("NOVAVOLT/NV1/FORMATION/F1/FORM-01", FactoryNodeKind.WorkCell)]
    [InlineData(Channel, FactoryNodeKind.Equipment)]
    public void Parse_PathOfAnyDepth_ReadsTheLevelFromTheNumberOfSegments(string value, FactoryNodeKind expected)
    {
        // A path may stop at any level, because different people ask questions at different levels: a
        // supervisor's dashboard asks about an area, a recall asks about a channel.
        var path = EquipmentPath.Parse(value);

        path.Kind.ShouldBe(expected);
    }

    [Fact]
    public void Parse_FullPath_ExposesEveryPart()
    {
        var path = EquipmentPath.Parse(Channel);

        path.EnterpriseCode.ShouldBe("NOVAVOLT");
        path.SiteId.ShouldBe("NV1");
        path.Code.ShouldBe("FORM-01-CH-0142");
        path.Segments.Length.ShouldBe(6);
        path.Value.ShouldBe(Channel);
    }

    [Fact]
    public void SiteId_OnAnEnterprisePath_IsNullRatherThanEmpty()
    {
        // The one level where "which plant" has no answer. Null forces callers to deal with it; an
        // empty string would sort and compare like a real site code and slip through a filter.
        var path = EquipmentPath.Parse("NOVAVOLT");

        path.SiteId.ShouldBeNull();
    }

    [Theory]
    [InlineData("NOVAVOLT/NV1", "NV1")]
    [InlineData("NOVAVOLT/DE1/MODULE/M1", "DE1")]
    [InlineData(Channel, "NV1")]
    public void SiteId_BelowEnterprise_IsAlwaysPresent(string value, string expected)
    {
        // AGENTS.md K3. Every level below the enterprise belongs to exactly one plant, and a leak
        // across that boundary is a security defect rather than a display bug.
        EquipmentPath.Parse(value).SiteId.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("", "empty")]
    [InlineData("NOVAVOLT//NV1", "doubled separator leaves an empty segment")]
    [InlineData("/NOVAVOLT/NV1", "leading separator")]
    [InlineData("NOVAVOLT/NV1/", "trailing separator")]
    [InlineData("novavolt/nv1", "lower case is a misconfigured scanner, not a synonym")]
    [InlineData("NOVAVOLT/Nv1", "mixed case")]
    [InlineData("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142/SENSOR-3", "seven segments, no such level")]
    [InlineData("NOVAVOLT/NV1/FORMATION/F1/FORM--01", "doubled hyphen is a second spelling")]
    [InlineData("NOVAVOLT/NV1/-FORMATION", "leading hyphen")]
    [InlineData("NOVAVOLT/NV1/FORMATION-", "trailing hyphen")]
    [InlineData("NOVAVOLT/1NV/FORMATION", "segment starts with a digit")]
    [InlineData("NOVAVOLT/NV1/FORM ATION", "space")]
    [InlineData("NOVAVOLT/NV_1", "underscore is not the separator this system uses")]
    public void TryParse_MalformedPath_ReturnsFalse(string? value, string reason)
    {
        var parsed = EquipmentPath.TryParse(value, out var path);

        parsed.ShouldBeFalse(reason);
        path.ShouldBeNull(reason);
    }

    [Fact]
    public void Parse_LowerCasePath_IsRejectedRatherThanUpperCased()
    {
        // Normalising would give one machine two nodes in the tree and split its history down the
        // middle. Codes are stencilled on the machine in upper case; anything else means the scanner
        // or the integration sending it is wrong, and that is worth finding out.
        Should.Throw<FormatException>(() => EquipmentPath.Parse(Channel.ToLowerInvariant()));
    }

    [Fact]
    public void Parent_ClimbsOneLevelAtATime()
    {
        var channel = EquipmentPath.Parse(Channel);

        channel.Parent!.Kind.ShouldBe(FactoryNodeKind.WorkCell);
        channel.Parent!.Parent!.Kind.ShouldBe(FactoryNodeKind.Line);
        channel.Parent!.Value.ShouldBe("NOVAVOLT/NV1/FORMATION/F1/FORM-01");
    }

    [Fact]
    public void Parent_OfAnEnterprise_IsNull()
    {
        EquipmentPath.Parse("NOVAVOLT").Parent.ShouldBeNull();
    }

    [Fact]
    public void Append_AddsOneLevelAndTheKindFollows()
    {
        var line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");

        var cell = line.Append("FORM-02");

        cell.Kind.ShouldBe(FactoryNodeKind.WorkCell);
        cell.Value.ShouldBe("NOVAVOLT/NV1/FORMATION/F1/FORM-02");
        cell.SiteId.ShouldBe("NV1");
    }

    [Fact]
    public void Append_BelowEquipment_IsRefused()
    {
        // Six levels, fixed. A sensor inside a channel is an attribute of the equipment, not a seventh
        // level — otherwise the depth no longer tells you what a path names.
        var channel = EquipmentPath.Parse(Channel);

        Should.Throw<InvalidOperationException>(() => channel.Append("SENSOR-3"));
    }

    [Fact]
    public void Append_MalformedCode_IsRefused()
    {
        var line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");

        Should.Throw<FormatException>(() => line.Append("form-02"));
    }

    [Fact]
    public void Equality_ComparesTheText_NotTheSegmentArray()
    {
        // The record's generated equality would compare the backing array by reference and call two
        // identical paths different — which would quietly break every dictionary keyed by path.
        var parsed = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01");
        var built = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION").Append("F1").Append("FORM-01");

        built.ShouldBe(parsed);
        built.GetHashCode().ShouldBe(parsed.GetHashCode());
    }

    [Fact]
    public void Equality_IsCaseSensitive()
    {
        var upper = EquipmentPath.Parse("NOVAVOLT/NV1");

        upper.Equals(null).ShouldBeFalse();
        upper.ShouldNotBe(EquipmentPath.Parse("NOVAVOLT/DE1"));
    }
}
