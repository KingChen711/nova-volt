using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Identity;

public sealed class EquipmentPathTests
{
    // Ví dụ đã tính trong docs/scope.md §2.1: charging channel 142 trên formation machine 01.
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
        // Path có thể dừng ở bất kỳ cấp nào, vì người khác nhau hỏi ở cấp khác nhau: dashboard của
        // supervisor hỏi về một area, còn một đợt recall hỏi về một channel.
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
        // Cấp duy nhất không trả lời được "nhà máy nào". Null buộc caller phải xử lý; chuỗi rỗng sẽ
        // sort và compare như site code thật rồi lọt qua filter.
        var path = EquipmentPath.Parse("NOVAVOLT");

        path.SiteId.ShouldBeNull();
    }

    [Theory]
    [InlineData("NOVAVOLT/NV1", "NV1")]
    [InlineData("NOVAVOLT/DE1/MODULE/M1", "DE1")]
    [InlineData(Channel, "NV1")]
    public void SiteId_BelowEnterprise_IsAlwaysPresent(string value, string expected)
    {
        // AGENTS.md K3. Mọi cấp dưới enterprise thuộc đúng một nhà máy, và rò rỉ qua ranh giới đó là
        // security defect chứ không phải display bug.
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
        // Normalize sẽ cho một máy hai node trong tree và chia đôi history của nó. Code được stencil
        // trên máy bằng chữ hoa; mọi dạng khác nghĩa là scanner hoặc integration gửi nó sai, và lỗi đó
        // cần được phát hiện.
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
        // Sáu cấp, cố định. Sensor trong channel là attribute của equipment, không phải cấp thứ bảy —
        // nếu không depth không còn nói được path đang gọi tên gì.
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
        // Equality do record sinh ra sẽ compare backing array theo reference và coi hai path giống hệt
        // nhau là khác — âm thầm làm hỏng mọi dictionary dùng path làm key.
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
