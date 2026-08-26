using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Nvm.Contracts;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.ContractTests.FactoryModel;

public sealed class RevisionActivatedV1Tests
{
    private const string Golden = "factory-model/revision-activated.v1.json";

    private static readonly JsonTypeInfo<CloudEventEnvelope<FactoryModelRevisionActivated>> EnvelopeInfo =
        NvmJsonSerializerContext.Default.CloudEventEnvelopeFactoryModelRevisionActivated;

    [Fact]
    public void GoldenV1_IsStillReadableByTodaysModel()
    {
        var json = GoldenFile.ReadText(Golden);

        var envelope = JsonSerializer.Deserialize(json, EnvelopeInfo);

        envelope.ShouldNotBeNull();
        envelope.Type.Value.ShouldBe("com.novavolt.factory-model.revision-activated.v1");
        envelope.Source.Value.ShouldBe("urn:novavolt:nv1:app-execution");
        envelope.Subject.ShouldBe("urn:factory-model:NV1");
        envelope.CorrelationId.ShouldBe("FMR-NV1-0012");
        envelope.PartitionKey.ShouldBe("NV1");
        envelope.Data.SiteId.ShouldBe("NV1");
        envelope.Data.Revision.ShouldBe(12);
        envelope.Data.NodeCount.ShouldBe(148);
        envelope.Data.EquipmentPathsAdded.Count.ShouldBe(2);
        envelope.Data.EquipmentPathsRemoved.ShouldHaveSingleItem()
            .ShouldBe("NOVAVOLT/NV1/ASSEMBLY/L2/STACK-07");
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
    public void GoldenV1_CarriesTheCloudEventsAttributeNamesVerbatim()
    {
        // The specification spells these lower case with no separators. A camelCase naming policy
        // would emit "specVersion" and "dataContentType" — still valid JSON, no longer CloudEvents,
        // and nothing in a .NET-to-.NET test would ever notice.
        var golden = GoldenFile.ReadNode(Golden).AsObject();

        foreach (var attribute in new[] { "specversion", "id", "type", "source", "time", "datacontenttype" })
        {
            golden.ContainsKey(attribute).ShouldBeTrue($"CloudEvents requires the attribute '{attribute}'");
        }
    }

    [Fact]
    public void EnvelopeId_AgreesWithTheEventIdInsideData()
    {
        // The envelope derives id from the payload, so these can only disagree if the golden file
        // itself is inconsistent — which is exactly what this checks, since the file is written by
        // hand rather than generated.
        var golden = GoldenFile.ReadNode(Golden).AsObject();

        var envelopeId = golden["id"]!.GetValue<string>();
        var dataEventId = golden["data"]!["eventId"]!.GetValue<string>();

        envelopeId.ShouldBe(dataEventId);
    }

    [Fact]
    public void OmittedOptionalAttribute_StaysOmittedAfterARoundTrip()
    {
        // causationid is absent from the golden file. CloudEvents treats an absent attribute and an
        // attribute set to null as different statements, so writing "causationid": null back would
        // change the meaning of the document.
        var envelope = JsonSerializer.Deserialize(GoldenFile.ReadText(Golden), EnvelopeInfo);

        envelope!.CausationId.ShouldBeNull();
        JsonNode.Parse(JsonSerializer.Serialize(envelope, EnvelopeInfo))!
            .AsObject().ContainsKey("causationid").ShouldBeFalse();
    }

    [Fact]
    public void Time_KeepsItsOffsetThroughARoundTrip()
    {
        // The single most valuable thing DateTimeOffset buys here. Site DE1 observes daylight saving
        // time, so a timestamp without an offset is ambiguous for one hour every autumn — and an
        // ambiguous timestamp in a legal record is a finding, not a rounding error.
        var envelope = JsonSerializer.Deserialize(GoldenFile.ReadText(Golden), EnvelopeInfo);

        envelope!.Time.Offset.ShouldBe(TimeSpan.Zero);

        JsonNode.Parse(JsonSerializer.Serialize(envelope, EnvelopeInfo))!["time"]!
            .GetValue<string>().ShouldBe("2026-08-25T03:15:42.128+00:00");
    }
}
