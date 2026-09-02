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
        // Phép join mà R-M1-6 đã cảnh báo. ce_id là cái mà một command handler dùng để dedupe, còn
        // source_event_id là cái mà ingestion dùng để dedupe; nếu file này từng cho thấy hai giá trị
        // khác nhau, cả hai cơ chế vẫn hoạt động nhưng không cái nào bảo vệ cái còn lại.
        var golden = GoldenFile.ReadNode(Golden).AsObject();

        golden["id"]!.GetValue<string>()
            .ShouldBe(golden["data"]!["eventId"]!.GetValue<string>());
    }

    [Fact]
    public void GoldenV1_KeepsAllThreeTimestampsApart()
    {
        // scope.md §7.3. device_timestamp trả lời "nó được đo lúc nào", gateway_timestamp "chúng ta
        // thấy nó lần đầu lúc nào", occurredAt "chúng ta chấp nhận nó lúc nào". Một golden file để hai
        // trong số chúng trùng nhau sẽ mất khả năng bắt được một phiên bản đã gộp các cột lại với nhau.
        var data = GoldenFile.ReadNode(Golden).AsObject()["data"]!.AsObject();

        var instants = new[] { "deviceTimestamp", "gatewayTimestamp", "occurredAt" }
            .Select(name => data[name]!.GetValue<string>())
            .ToArray();

        instants.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void GoldenV1_OmitsTheValueFieldsThatDoNotApply()
    {
        // WhenWritingNull giữ các optional chưa set ra khỏi document. Một reading kiểu "real" mà còn
        // mang theo "integerValue": null sẽ là đang tuyên bố rằng integer được biết là không có gì.
        var data = GoldenFile.ReadNode(Golden).AsObject()["data"]!.AsObject();

        data.ContainsKey("realValue").ShouldBeTrue();

        foreach (var absent in new[] { "integerValue", "booleanValue", "textValue", "unitId" })
        {
            data.ContainsKey(absent).ShouldBeFalse($"'{absent}' does not apply to a real-valued reading");
        }
    }
}
