using Nvm.Contracts.CloudEvents;

namespace Nvm.UnitTests.Contracts;

public sealed class RoutingKeyTests
{
    // The worked example from docs/scope.md §7.4. Note the upper-case site.
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
        // The trap this whole type exists to close. AMQP matches routing keys byte for byte, so a
        // publisher on nvm.NV1.* and a consumer bound to nvm.nv1.# never meet — and the broker
        // reports nothing at all. Refusing the lower-case spelling is the only moment anyone finds
        // out, so it has to be a hard failure rather than a tolerated variant.
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
        // A routing key and the CloudEvents type attribute describe the same event in two different
        // strings. This is the assertion that they cannot drift: the key carries the type itself
        // rather than a copy of its parts.
        var key = RoutingKey.Parse(UnitSerializedAtNv1);

        key.EventType.Value.ShouldBe("com.novavolt.traceability.unit-serialized.v1");
    }
}
