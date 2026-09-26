using System.Text.Json;
using System.Text.Json.Nodes;
using Nvm.Contracts;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Contracts.Events.Quality;
using Nvm.Contracts.Events.Traceability;

namespace Nvm.ContractTests;

/// <summary>
/// Golden file của các event từ M5 trở đi. Mỗi event thêm một dòng: file JSON đã ghi không bao giờ sửa,
/// model hiện tại phải đọc lại và ghi ra đúng document đó.
/// </summary>
public sealed class DomainEventGoldenTests
{
    public static TheoryData<string, Type> Goldens => new()
    {
        { "quality/unit-quarantined.v1.json", typeof(CloudEventEnvelope<UnitQuarantined>) },
        { "traceability/unit-assembled-into.v1.json", typeof(CloudEventEnvelope<UnitAssembledInto>) },
        { "traceability/unit-removed-from.v1.json", typeof(CloudEventEnvelope<UnitRemovedFrom>) },
        { "traceability/genealogy-correction-recorded.v1.json", typeof(CloudEventEnvelope<GenealogyCorrectionRecorded>) },
        { "material/material-lot-consumed.v1.json", typeof(CloudEventEnvelope<MaterialLotConsumed>) },
        { "production-execution/roll-coated.v1.json", typeof(CloudEventEnvelope<RollCoated>) },
    };

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Golden_IsReadableAndRoundTripsLosslessly(string golden, Type envelopeType)
    {
        var text = GoldenFile.ReadText(golden);
        var info = NvmJsonSerializerContext.Default.GetTypeInfo(envelopeType);
        info.ShouldNotBeNull($"{envelopeType} must be registered in NvmJsonSerializerContext");
        var envelope = JsonSerializer.Deserialize(text, info);
        envelope.ShouldNotBeNull();
        JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(envelope, info)), JsonNode.Parse(text))
            .ShouldBeTrue("wire fields and values of a recorded version are immutable");
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Golden_DeclaresItsOwnTypeIdentityAndSite(string golden, Type envelopeType)
    {
        var node = GoldenFile.ReadNode(golden);
        var dataType = envelopeType.GetGenericArguments()[0];
        node["type"]!.GetValue<string>().ShouldBe(EventTypeName.Of(dataType).Value);
        node["id"]!.GetValue<string>().ShouldBe(node["data"]!["eventId"]!.GetValue<string>());
        node["time"]!.GetValue<string>().ShouldBe(node["data"]!["occurredAt"]!.GetValue<string>());
        node["data"]!["siteId"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
    }
}
