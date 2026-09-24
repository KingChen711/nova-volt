using System.Text.Json.Nodes;
using Nvm.EventStore;
using Nvm.Kernel.EventSourcing;

namespace Nvm.UnitTests.EventSourcing;

public sealed class EventUpcasterChainTests
{
    private static string HistoricalV1 => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "EventSourcing", "Fixtures", "ProductionUnitSerialized.v1.json")).TrimEnd();

    [Fact]
    public void HistoricalV1_ReplaysThroughV2AndV3_WithoutChangingFixture()
    {
        var chain = new EventUpcasterChain([new SerializedV1ToV2(), new SerializedV2ToV3()]);
        var first = chain.Upcast("unit-serialized", 1, HistoricalV1);
        var second = chain.Upcast("unit-serialized", 1, HistoricalV1);

        first.Version.ShouldBe(3);
        first.PayloadJson.ShouldBe(second.PayloadJson);
        var data = JsonNode.Parse(first.PayloadJson)!;
        data["serialNumber"]!.GetValue<string>().ShouldBe("NV1CL16238A00123");
        data["state"]!.GetValue<string>().ShouldBe("Made");
        data["unitKind"]!.GetValue<string>().ShouldBe("Cell");
        HistoricalV1.ShouldBe("""{"serial":"NV1CL16238A00123","state":"Made"}""");
    }

    [Fact]
    public void MissingMiddleVersion_RejectsReplayInsteadOfSilentlyReturningOldShape()
    {
        var chain = new EventUpcasterChain([new SerializedV2ToV3()]);
        Should.Throw<InvalidOperationException>(() => chain.Upcast("unit-serialized", 1, HistoricalV1));
    }

    [Fact]
    public void DuplicateStep_IsRejectedAtRegistration()
    {
        Should.Throw<ArgumentException>(() => new EventUpcasterChain([new SerializedV1ToV2(), new SerializedV1ToV2()]));
    }

    private sealed class SerializedV1ToV2 : IEventUpcaster
    {
        public string EventType => "unit-serialized";
        public int FromVersion => 1;
        public int ToVersion => 2;
        public string Upcast(string payloadJson)
        {
            var node = JsonNode.Parse(payloadJson)!;
            node["serialNumber"] = node["serial"]!.GetValue<string>();
            node.AsObject().Remove("serial");
            return node.ToJsonString();
        }
    }

    private sealed class SerializedV2ToV3 : IEventUpcaster
    {
        public string EventType => "unit-serialized";
        public int FromVersion => 2;
        public int ToVersion => 3;
        public string Upcast(string payloadJson)
        {
            var node = JsonNode.Parse(payloadJson)!;
            node["unitKind"] = "Cell";
            return node.ToJsonString();
        }
    }
}
