using Nvm.Contracts.CloudEvents;

namespace Nvm.UnitTests.Contracts;

public sealed class EventSourceTests
{
    // The worked example from docs/scope.md §7.4. Note the lower-case site, unlike a routing key.
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
        // The URN spelling and the canonical spelling are different on purpose. Everywhere else in
        // the system a SiteId is upper case — in a serial number, in an equipment path, in the
        // site_id claim from Keycloak — so a caller comparing source.SiteId against any of those
        // must not have to remember that this one came out of a URN.
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
        // Callers hand over the canonical form and let this type do the lower-casing. Accepting both
        // spellings would mean two callers can produce the same URN from different inputs, and the
        // SiteId property would then mean different things depending on who built the object.
        Should.Throw<FormatException>(() => EventSource.Create("nv1", "app-execution"));
    }
}
