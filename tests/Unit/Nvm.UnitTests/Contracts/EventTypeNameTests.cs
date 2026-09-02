using Nvm.Contracts.CloudEvents;

namespace Nvm.UnitTests.Contracts;

public sealed class EventTypeNameTests
{
    // Ví dụ minh họa từ docs/scope.md §7.4.
    private const string UnitSerialized = "com.novavolt.traceability.unit-serialized.v1";

    [Fact]
    public void Create_ContextNameAndVersion_ProducesTheDocumentedString()
    {
        var type = EventTypeName.Create("traceability", "unit-serialized", 1);

        type.Value.ShouldBe(UnitSerialized);
    }

    [Fact]
    public void Parse_DocumentedString_ExposesEveryPart()
    {
        var type = EventTypeName.Parse(UnitSerialized);

        type.Context.ShouldBe("traceability");
        type.Name.ShouldBe("unit-serialized");
        type.Version.ShouldBe(1);
    }

    [Theory]
    [InlineData("traceability", "unit-serialized", 1)]
    [InlineData("quality", "unit-quarantined", 3)]
    [InlineData("production-execution", "formation-run-completed", 12)]
    public void Parse_WhatCreateProduced_RoundTripsToTheSameValue(string context, string name, int version)
    {
        var created = EventTypeName.Create(context, name, version);

        var parsed = EventTypeName.Parse(created.Value);

        parsed.ShouldBe(created);
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("", "empty")]
    [InlineData("com.novavolt.traceability.unit-serialized", "no version segment")]
    [InlineData("com.novavolt.traceability.unit-serialized.1", "version has no v marker")]
    [InlineData("com.novavolt.traceability.unit-serialized.v", "v marker with no number")]
    [InlineData("com.novavolt.traceability.unit-serialized.vX", "version is not a number")]
    [InlineData("com.novavolt.traceability.unit-serialized.v0", "version 0 is below 1")]
    [InlineData("com.novavolt.traceability.unit-serialized.v-1", "negative version")]
    [InlineData("com.novavolt.traceability.unit-serialized.v+1", "signed version")]
    [InlineData("com.novavolt.traceability.unit-serialized.v01", "leading zero would round-trip to v1")]
    [InlineData("com.contoso.traceability.unit-serialized.v1", "wrong reverse-DNS prefix")]
    [InlineData("com.novavolt.Traceability.unit-serialized.v1", "context is not lower case")]
    [InlineData("com.novavolt.traceability.unit_serialized.v1", "underscore instead of hyphen")]
    [InlineData("com.novavolt.traceability.unit--serialized.v1", "double hyphen is a second spelling")]
    [InlineData("com.novavolt.traceability.-unit-serialized.v1", "leading hyphen")]
    [InlineData("com.novavolt.traceability.unit-serialized-.v1", "trailing hyphen")]
    [InlineData("com.novavolt..unit-serialized.v1", "empty context")]
    [InlineData("com.novavolt.traceability.unit.serialized.v1", "extra segment")]
    public void TryParse_MalformedString_ReturnsFalse(string? value, string reason)
    {
        var parsed = EventTypeName.TryParse(value, out var type);

        parsed.ShouldBeFalse(reason);
        type.ShouldBeNull(reason);
    }

    [Fact]
    public void Parse_LeadingZeroVersion_IsRejectedRatherThanNormalised()
    {
        // int.TryParse chấp nhận "01" một cách thoải mái. Nếu chấp nhận nó, hai cách viết của cùng
        // một event type sẽ cùng tồn tại trong store, mà chỉ một trong hai từng khớp được với một
        // routing key.
        Should.Throw<FormatException>(() => EventTypeName.Parse("com.novavolt.quality.measurement-recorded.v01"));
    }

    [Theory]
    [InlineData("Traceability", "unit-serialized", "context is not lower case")]
    [InlineData("traceability", "UnitSerialized", "name is PascalCase, not kebab-case")]
    [InlineData("traceability", "2fast", "name starts with a digit")]
    [InlineData("", "unit-serialized", "empty context")]
    public void Create_MalformedPart_Throws(string context, string name, string reason)
    {
        Should.Throw<FormatException>(() => EventTypeName.Create(context, name, 1), reason);
    }

    [Fact]
    public void Create_VersionBelowOne_Throws()
    {
        Should.Throw<FormatException>(() => EventTypeName.Create("traceability", "unit-serialized", 0));
    }
}
