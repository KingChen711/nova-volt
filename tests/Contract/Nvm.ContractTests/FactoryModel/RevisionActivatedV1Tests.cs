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
        // Đặc tả viết những cái này bằng chữ thường và không có dấu phân cách. Một naming policy
        // camelCase sẽ phát ra "specVersion" và "dataContentType" — vẫn là JSON hợp lệ, nhưng không
        // còn là CloudEvents, và không gì trong một test .NET-to-.NET sẽ nhận ra điều đó.
        var golden = GoldenFile.ReadNode(Golden).AsObject();

        foreach (var attribute in new[] { "specversion", "id", "type", "source", "time", "datacontenttype" })
        {
            golden.ContainsKey(attribute).ShouldBeTrue($"CloudEvents requires the attribute '{attribute}'");
        }
    }

    [Fact]
    public void EnvelopeId_AgreesWithTheEventIdInsideData()
    {
        // Envelope suy ra id từ payload, nên hai giá trị này chỉ có thể bất đồng nếu bản thân golden
        // file không nhất quán — đó chính xác là điều cái này kiểm tra, vì file được viết tay thay vì
        // sinh ra tự động.
        var golden = GoldenFile.ReadNode(Golden).AsObject();

        var envelopeId = golden["id"]!.GetValue<string>();
        var dataEventId = golden["data"]!["eventId"]!.GetValue<string>();

        envelopeId.ShouldBe(dataEventId);
    }

    [Fact]
    public void OmittedOptionalAttribute_StaysOmittedAfterARoundTrip()
    {
        // causationid vắng mặt trong golden file. CloudEvents coi một attribute vắng mặt và một
        // attribute được đặt thành null là hai phát biểu khác nhau, nên ghi lại "causationid": null
        // sẽ làm thay đổi ý nghĩa của document.
        var envelope = JsonSerializer.Deserialize(GoldenFile.ReadText(Golden), EnvelopeInfo);

        envelope!.CausationId.ShouldBeNull();
        JsonNode.Parse(JsonSerializer.Serialize(envelope, EnvelopeInfo))!
            .AsObject().ContainsKey("causationid").ShouldBeFalse();
    }

    [Fact]
    public void Time_KeepsItsOffsetThroughARoundTrip()
    {
        // Thứ giá trị nhất mà DateTimeOffset mang lại ở đây. Site DE1 quan sát daylight saving time,
        // nên một timestamp không có offset sẽ mập mờ trong một giờ mỗi mùa thu — và một timestamp
        // mập mờ trong một legal record là một finding, không phải một lỗi làm tròn.
        var envelope = JsonSerializer.Deserialize(GoldenFile.ReadText(Golden), EnvelopeInfo);

        envelope!.Time.Offset.ShouldBe(TimeSpan.Zero);

        JsonNode.Parse(JsonSerializer.Serialize(envelope, EnvelopeInfo))!["time"]!
            .GetValue<string>().ShouldBe("2026-08-25T03:15:42.128+00:00");
    }
}
