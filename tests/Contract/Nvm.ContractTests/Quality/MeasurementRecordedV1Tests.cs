using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Nvm.Contracts;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.Quality;

namespace Nvm.ContractTests.Quality;

public sealed class MeasurementRecordedV1Tests
{
    private const string Golden = "quality/measurement-recorded.v1.json";

    private static readonly JsonTypeInfo<CloudEventEnvelope<MeasurementRecorded>> EnvelopeInfo =
        NvmJsonSerializerContext.Default.CloudEventEnvelopeMeasurementRecorded;

    [Fact]
    public void GoldenV1_IsStillReadableByTodaysModel()
    {
        var envelope = JsonSerializer.Deserialize(GoldenFile.ReadText(Golden), EnvelopeInfo);

        envelope.ShouldNotBeNull();
        envelope.Type.Value.ShouldBe("com.novavolt.quality.measurement-recorded.v1");
        envelope.Source.Value.ShouldBe("urn:novavolt:nv1:ingestion");
        envelope.PartitionKey.ShouldBe("NV1");
        envelope.Data.SiteId.ShouldBe("NV1");
        envelope.Data.SignalCode.ShouldBe("Formation/Capacity");
        envelope.Data.ClockQuality.ShouldBe("Good");
        envelope.Data.ValueKind.ShouldBe("real");
        envelope.Data.RealValue.ShouldBe(4.812);
        envelope.Data.IntegerValue.ShouldBeNull();
    }

    [Fact]
    public void TodaysModel_WritesBackTheSameV1Document()
    {
        var envelope = JsonSerializer.Deserialize(GoldenFile.ReadText(Golden), EnvelopeInfo);

        var rewritten = JsonNode.Parse(JsonSerializer.Serialize(envelope!, EnvelopeInfo));

        JsonNode.DeepEquals(rewritten, GoldenFile.ReadNode(Golden)).ShouldBeTrue(
            "reading and writing a v1 envelope must be lossless; anything else means the wire shape moved");
    }

    [Fact]
    public void EnvelopeId_IsTheSourceEventIdInsideData()
    {
        // The join R-M1-6 warned about. ce_id is what a command handler deduplicates on and
        // source_event_id is what ingestion deduplicates on; if this file ever shows two different
        // values, both mechanisms keep working and neither one protects the other.
        var golden = GoldenFile.ReadNode(Golden).AsObject();

        golden["id"]!.GetValue<string>()
            .ShouldBe(golden["data"]!["eventId"]!.GetValue<string>());
    }

    [Fact]
    public void GoldenV1_KeepsAllThreeTimestampsApart()
    {
        // scope.md §7.3. device_timestamp answers "when was it measured", gateway_timestamp "when did
        // we first see it", occurredAt "when did we accept it". A golden file that let two of them
        // coincide would stop being able to catch a version that collapsed the columns.
        var data = GoldenFile.ReadNode(Golden).AsObject()["data"]!.AsObject();

        var instants = new[] { "deviceTimestamp", "gatewayTimestamp", "occurredAt" }
            .Select(name => data[name]!.GetValue<string>())
            .ToArray();

        instants.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void GoldenV1_OmitsTheValueFieldsThatDoNotApply()
    {
        // WhenWritingNull keeps unset optionals out of the document. A reading of kind "real" that
        // also carried "integerValue": null would be claiming the integer is known to be nothing.
        var data = GoldenFile.ReadNode(Golden).AsObject()["data"]!.AsObject();

        data.ContainsKey("realValue").ShouldBeTrue();

        foreach (var absent in new[] { "integerValue", "booleanValue", "textValue", "unitId" })
        {
            data.ContainsKey(absent).ShouldBeFalse($"'{absent}' does not apply to a real-valued reading");
        }
    }
}
