using System.Text.Json;
using System.Text.Json.Nodes;
using Nvm.Contracts;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.Traceability;

namespace Nvm.ContractTests.Traceability;

public sealed class UnitEventV1Tests
{
    [Theory]
    [InlineData("unit-serialized", typeof(CloudEventEnvelope<ProductionUnitSerialized>))]
    [InlineData("process-step-started", typeof(CloudEventEnvelope<ProcessStepStarted>))]
    [InlineData("process-step-completed", typeof(CloudEventEnvelope<ProcessStepCompleted>))]
    [InlineData("unit-measurement-recorded", typeof(CloudEventEnvelope<UnitMeasurementRecorded>))]
    [InlineData("duplicate-serial-detected", typeof(CloudEventEnvelope<DuplicateSerialDetected>))]
    public void GoldenV1_RemainsReadableAndLossless_WithIndependentClocks(string eventName, Type envelopeType)
    {
        var golden = GoldenFile.ReadText($"traceability/{eventName}.v1.json");
        var info = NvmJsonSerializerContext.Default.GetTypeInfo(envelopeType);
        info.ShouldNotBeNull();
        var envelope = JsonSerializer.Deserialize(golden, info);
        envelope.ShouldNotBeNull();
        var original = JsonNode.Parse(golden)!;
        var rewritten = JsonNode.Parse(JsonSerializer.Serialize(envelope, info));
        JsonNode.DeepEquals(original, rewritten).ShouldBeTrue("v1 wire fields and values are immutable");
        original["type"]!.GetValue<string>().ShouldBe($"com.novavolt.traceability.{eventName}.v1");
        original["id"]!.GetValue<string>().ShouldBe(original["data"]!["eventId"]!.GetValue<string>());
        original["time"]!.GetValue<string>().ShouldBe(original["data"]!["occurredAt"]!.GetValue<string>());
        original["time"]!.GetValue<string>().ShouldNotBe(original["data"]!["recordedAt"]!.GetValue<string>());
        original["data"]!["siteId"]!.GetValue<string>().ShouldBe("NV1");
        original["data"]!["serialNumber"]!.GetValue<string>().ShouldBe("NV1CL16267A70485");
    }
}
