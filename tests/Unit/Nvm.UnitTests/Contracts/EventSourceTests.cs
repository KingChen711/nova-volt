using Nvm.Contracts.CloudEvents;

namespace Nvm.UnitTests.Contracts;

public sealed class EventSourceTests
{
    // Ví dụ minh họa từ docs/scope.md §7.4. Lưu ý site viết thường, khác với routing key.
    private const string ExecutionAtNv1 = "urn:novavolt:nv1:app-execution";

    [Fact]
    public void Create_SiteAndApplication_LowerCasesTheSiteInTheUrn()
    {
        var source = EventSource.Create("NV1", "app-execution");

        source.Value.ShouldBe(ExecutionAtNv1);
    }

    [Fact]
    public void Create_SiteAndApplication_KeepsTheSiteUpperCaseOnTheObject()
    {
        // Cách viết trong URN và cách viết chuẩn cố tình khác nhau. Ở mọi nơi khác trong hệ thống,
        // SiteId đều viết hoa — trong serial number, trong equipment path, trong claim site_id từ
        // Keycloak — nên một caller so sánh source.SiteId với bất kỳ cái nào trong số đó không cần
        // phải nhớ rằng giá trị này đến từ một URN.
        var source = EventSource.Create("NV1", "app-execution");

        source.SiteId.ShouldBe("NV1");
    }

    [Fact]
    public void Parse_UrnWithLowerCaseSite_ReturnsTheCanonicalUpperCaseSite()
    {
        var source = EventSource.Parse(ExecutionAtNv1);

        source.SiteId.ShouldBe("NV1");
        source.Application.ShouldBe("app-execution");
    }

    [Theory]
    [InlineData("NV1", "app-execution")]
    [InlineData("DE1", "ingestion")]
    [InlineData("NV1", "edge-gateway")]
    public void Parse_WhatCreateProduced_RoundTripsToTheSameValue(string siteId, string application)
    {
        var created = EventSource.Create(siteId, application);

        var parsed = EventSource.Parse(created.Value);

        parsed.ShouldBe(created);
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("", "empty")]
    [InlineData("urn:novavolt:nv1", "no application segment")]
    [InlineData("urn:contoso:nv1:app-execution", "wrong urn namespace")]
    [InlineData("urn:novavolt:NV1:app-execution", "site is not lower case in the urn")]
    [InlineData("urn:novavolt:nv1:App-Execution", "application is not lower case")]
    [InlineData("urn:novavolt:nv-1:app-execution", "hyphen is not allowed in a site code")]
    [InlineData("urn:novavolt::app-execution", "empty site")]
    [InlineData("urn:novavolt:nv1:app-execution:extra", "extra segment")]
    public void TryParse_MalformedString_ReturnsFalse(string? value, string reason)
    {
        var parsed = EventSource.TryParse(value, out var source);

        parsed.ShouldBeFalse(reason);
        source.ShouldBeNull(reason);
    }

    [Fact]
    public void Create_LowerCaseSite_Throws()
    {
        // Caller đưa vào dạng chuẩn và để type này tự chuyển sang chữ thường. Nếu chấp nhận cả hai
        // cách viết thì hai caller có thể tạo ra cùng một URN từ input khác nhau, và property SiteId
        // khi đó sẽ mang ý nghĩa khác nhau tùy vào ai đã tạo ra object.
        Should.Throw<FormatException>(() => EventSource.Create("nv1", "app-execution"));
    }
}
