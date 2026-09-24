using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Nvm.Contracts;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.ProductionExecution;

namespace Nvm.ContractTests.ProductionExecution;

public sealed class DataCollectionRecordedV1Tests
{
    private const string Golden = "production-execution/data-collection-recorded.v1.json";

    private static readonly JsonTypeInfo<CloudEventEnvelope<DataCollectionRecorded>> EnvelopeInfo =
        NvmJsonSerializerContext.Default.CloudEventEnvelopeDataCollectionRecorded;

    [Fact]
    public void GoldenV1_IsStillReadableByTodaysModel()
    {
        var envelope = JsonSerializer.Deserialize(GoldenFile.ReadText(Golden), EnvelopeInfo);

        envelope.ShouldNotBeNull();
        envelope.Type.Value.ShouldBe("com.novavolt.production-execution.data-collection-recorded.v1");
        envelope.Source.Value.ShouldBe("urn:novavolt:nv1:app-execution");
        envelope.PartitionKey.ShouldBe("NV1");
        envelope.Data.SiteId.ShouldBe("NV1");
        envelope.Data.StepCode.ShouldBe("EOL");
        envelope.Data.SignalCode.ShouldBe("PackVoltage");
        envelope.Data.UnitOfMeasure.ShouldBe("V");
        // Giá trị là decimal: 401.25 phải đọc lại chính xác, không phải xấp xỉ double.
        envelope.Data.Value.ShouldBe(401.25m);
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
    public void EnvelopeId_IsTheEventIdInsideData()
    {
        // ce_id = idempotency key của command. Lệch nhau thì dedup ở pipeline và dedup ở consumer key
        // theo hai giá trị khác nhau và không cái nào bảo vệ cái còn lại (docs/scope.md §7.2).
        var golden = GoldenFile.ReadNode(Golden).AsObject();

        golden["id"]!.GetValue<string>()
            .ShouldBe(golden["data"]!["eventId"]!.GetValue<string>());
    }

    [Fact]
    public void EnvelopeTime_IsTheSubmissionOccurredAt()
    {
        // CloudEvents time = thời điểm người vận hành xác nhận nhập (occurredAt), không phải recordedAt.
        var golden = GoldenFile.ReadNode(Golden).AsObject();

        golden["time"]!.GetValue<string>()
            .ShouldBe(golden["data"]!["occurredAt"]!.GetValue<string>());
    }

    [Fact]
    public void GoldenV1_KeepsSubmissionAndServerClocksApart()
    {
        // occurredAt (người dùng xác nhận) và recordedAt (server TimeProvider) là hai sự thật khác nhau.
        // Một golden để chúng trùng nhau sẽ mất khả năng bắt một phiên bản gộp hai đồng hồ lại.
        var data = GoldenFile.ReadNode(Golden).AsObject()["data"]!.AsObject();

        data.ContainsKey("recordedAt").ShouldBeTrue();
        data["recordedAt"]!.GetValue<string>()
            .ShouldNotBe(data["occurredAt"]!.GetValue<string>());
    }
}
