using Nvm.Contracts.CloudEvents;

namespace Nvm.UnitTests.Contracts;

public sealed class RoutingKeyTests
{
    // Ví dụ minh họa từ docs/scope.md §7.4. Lưu ý site viết hoa.
    private const string UnitSerializedAtNv1 = "nvm.NV1.traceability.unit-serialized.v1";

    private static readonly EventTypeName UnitSerialized =
        EventTypeName.Create("traceability", "unit-serialized", 1);

    [Fact]
    public void Create_SiteAndEventType_ProducesTheDocumentedString()
    {
        var key = RoutingKey.Create("NV1", UnitSerialized);

        key.Value.ShouldBe(UnitSerializedAtNv1);
    }

    [Fact]
    public void Parse_DocumentedString_ExposesSiteAndEventType()
    {
        var key = RoutingKey.Parse(UnitSerializedAtNv1);

        key.SiteId.ShouldBe("NV1");
        key.EventType.ShouldBe(UnitSerialized);
    }

    [Theory]
    [InlineData("NV1")]
    [InlineData("DE1")]
    public void Parse_WhatCreateProduced_RoundTripsToTheSameValue(string siteId)
    {
        var created = RoutingKey.Create(siteId, UnitSerialized);

        var parsed = RoutingKey.Parse(created.Value);

        parsed.ShouldBe(created);
    }

    [Fact]
    public void TryParse_LowerCaseSite_IsRejected()
    {
        // Cái bẫy mà toàn bộ type này tồn tại để chặn lại. AMQP so khớp routing key từng byte một, nên
        // một publisher trên nvm.NV1.* và một consumer bind vào nvm.nv1.# sẽ không bao giờ gặp nhau —
        // và broker không báo lỗi gì cả. Từ chối cách viết chữ thường là khoảnh khắc duy nhất ai đó
        // phát hiện ra, nên nó phải là một lỗi cứng chứ không phải một biến thể được dung thứ.
        var parsed = RoutingKey.TryParse("nvm.nv1.traceability.unit-serialized.v1", out var key);

        parsed.ShouldBeFalse();
        key.ShouldBeNull();
    }

    [Fact]
    public void Create_LowerCaseSite_Throws()
    {
        Should.Throw<FormatException>(() => RoutingKey.Create("nv1", UnitSerialized));
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("", "empty")]
    [InlineData("nvm.NV1.traceability.unit-serialized", "no version segment")]
    [InlineData("nvm.NV1.traceability.unit-serialized.1", "version has no v marker")]
    [InlineData("nvm.NV1.traceability.unit-serialized.v0", "version 0 is below 1")]
    [InlineData("nvm.NV1.traceability.unit-serialized.v01", "leading zero would round-trip differently")]
    [InlineData("mes.NV1.traceability.unit-serialized.v1", "wrong prefix")]
    [InlineData("nvm.NV-1.traceability.unit-serialized.v1", "hyphen is not allowed in a site code")]
    [InlineData("nvm..traceability.unit-serialized.v1", "empty site")]
    [InlineData("nvm.NV1.Traceability.unit-serialized.v1", "context is not lower case")]
    [InlineData("nvm.NV1.traceability.unit-serialized.v1.extra", "extra segment")]
    public void TryParse_MalformedString_ReturnsFalse(string? value, string reason)
    {
        var parsed = RoutingKey.TryParse(value, out var key);

        parsed.ShouldBeFalse(reason);
        key.ShouldBeNull(reason);
    }

    [Fact]
    public void EventType_OnAParsedKey_MatchesTheCloudEventsTypeForTheSameEvent()
    {
        // Một routing key và attribute type của CloudEvents mô tả cùng một event bằng hai chuỗi khác
        // nhau. Đây là assertion rằng chúng không thể lệch nhau: key mang chính cái type đó chứ không
        // phải một bản sao các phần của nó.
        var key = RoutingKey.Parse(UnitSerializedAtNv1);

        key.EventType.Value.ShouldBe("com.novavolt.traceability.unit-serialized.v1");
    }
}
