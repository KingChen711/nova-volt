using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Npgsql;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Traceability;
using Nvm.Kernel.EventSourcing;
using Nvm.Projections;

namespace Nvm.IntegrationTests;

public sealed class ProjectedPomTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task LiveLifecycleAndLateHold_ChangeODataAndWip_WithoutLeakingSitesOrReturningFixtures()
    {
        var fixture = new PomOperatorFixture();
        await fixture.InitializeAsync();
        try
        {
            await using var database = NpgsqlDataSource.Create(fixture.ConnectionString);
            await ProjectionSchemaMigrator.UpgradeAsync(database, Ct);
            await ProjectionPomConnector.ConnectAsync(database, Ct);
            var inbox = new ProductionUnitProjectionInbox(database);
            const string serial = "NV1CL16267A70485";
            const string deSerial = "DE1PM16267A70485";
            await Deliver(1, serial, 1, new ProductionUnitSerialized(Guid.NewGuid(), Now, Now, "NV1", serial,
                "Cell", "NV-CELL-DEMO", "WO-LIVE", "r1", "operator"));
            // Incident có thể tới inbox trước fact tạo unit; hold không được mất khi unit xuất hiện.
            await Deliver(6, "duplicate:de-before-birth", 1, new DuplicateSerialDetected(Guid.NewGuid(),
                Now, Now, "DE1", deSerial, "de-before-birth", "operator", "DUPLICATE_SERIAL"));
            await Deliver(5, deSerial, 1, new ProductionUnitSerialized(Guid.NewGuid(), Now, Now, "DE1", deSerial,
                "Pack", "NV-PACK-DEMO", new string('W', 100), "r1", "operator"));
            await Deliver(7, deSerial, 2, new ProcessStepStarted(Guid.NewGuid(), Now, Now, "DE1", deSerial,
                new string('S', 20), new string('R', 100), "NOVAVOLT/DE1/PACK/OTHER-LINE/EOL-01", "operator"));
            await Deliver(2, serial, 2, new ProcessStepStarted(Guid.NewGuid(), Now, Now, "NV1", serial,
                "STACK", "run-live", "NOVAVOLT/NV1/ASSEMBLY/L1/STACK-01", "operator"));
            using var running = await fixture.SendAsync($"ProductionUnits('{serial}')", fixture.Token());
            running.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var runningJson = JsonDocument.Parse(await running.Content.ReadAsStringAsync(Ct));
            runningJson.RootElement.GetProperty("ExecutionState").GetString().ShouldBe("Running");
            runningJson.RootElement.GetProperty("Resource").GetString().ShouldBe("STACK-01");
            runningJson.RootElement.GetProperty("QualityState").GetString().ShouldBe("Pending");

            await Deliver(4, serial, 3, new ProcessStepCompleted(Guid.NewGuid(), Now, Now, "NV1", serial,
                "STACK", "run-live", "operator"));
            using var completed = await fixture.SendAsync($"ProductionUnits('{serial}')", fixture.Token());
            var priorTag = completed.Headers.ETag;
            priorTag.ShouldNotBeNull();
            // This incident commits before sequence 4 but is delivered later. ETag must still change.
            var duplicate = new DuplicateSerialDetected(Guid.NewGuid(), Now, Now, "NV1", serial,
                "duplicate-attempt", "operator", "DUPLICATE_SERIAL");
            await Deliver(3, "duplicate:duplicate-attempt", 1, duplicate);
            using var held = await fixture.SendAsync($"ProductionUnits('{serial}')", fixture.Token());
            held.Headers.ETag.ShouldNotBe(priorTag);
            using var heldJson = JsonDocument.Parse(await held.Content.ReadAsStringAsync(Ct));
            heldJson.RootElement.GetProperty("ExecutionState").GetString().ShouldBe("Completed");
            heldJson.RootElement.GetProperty("QualityState").GetString().ShouldBe("Held");
            heldJson.RootElement.GetProperty("BlockingReasonCode").GetString().ShouldBe("DUPLICATE_SERIAL");
            heldJson.RootElement.GetProperty("LocationState").GetString().ShouldBe("AtStation");
            await Deliver(3, "duplicate:duplicate-attempt", 1, duplicate);

            using var board = await fixture.SendAsync("WipBoard?$count=true", fixture.Token());
            using var boardJson = JsonDocument.Parse(await board.Content.ReadAsStringAsync(Ct));
            var group = boardJson.RootElement.GetProperty("value").EnumerateArray().Single();
            group.GetProperty("UnitCount").GetInt32().ShouldBe(1);
            group.GetProperty("QualityState").GetString().ShouldBe("Held");
            using var units = await fixture.SendAsync("ProductionUnits?$count=true", fixture.Token());
            using var unitsJson = JsonDocument.Parse(await units.Content.ReadAsStringAsync(Ct));
            unitsJson.RootElement.GetProperty("@odata.count").GetInt32().ShouldBe(1);
            using var crossSite = await fixture.SendAsync($"ProductionUnits('{deSerial}')", fixture.Token());
            crossSite.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            using var de = await fixture.SendAsync("ProductionUnits?$count=true", fixture.Token("DE1"));
            using var deJson = JsonDocument.Parse(await de.Content.ReadAsStringAsync(Ct));
            var deUnit = deJson.RootElement.GetProperty("value").EnumerateArray().Single();
            deUnit.GetProperty("SerialNumber").GetString().ShouldBe(deSerial);
            deUnit.GetProperty("QualityState").GetString().ShouldBe("Held");
            deUnit.GetProperty("Line").GetString().ShouldBe("M1");
            deUnit.GetProperty("WorkOrderId").GetString()!.Length.ShouldBe(100);
            deUnit.GetProperty("OperationRunId").GetString()!.Length.ShouldBe(100);
            deUnit.GetProperty("StepCode").GetString()!.Length.ShouldBe(20);

            using var deBoard = await fixture.SendAsync("WipBoard", fixture.Token("DE1"));
            deBoard.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var deBoardJson = JsonDocument.Parse(await deBoard.Content.ReadAsStringAsync(Ct));
            var deGroup = deBoardJson.RootElement.GetProperty("value").EnumerateArray().Single();
            deGroup.GetProperty("Id").GetString().ShouldBe($"DE1:M1:{new string('S', 20)}:Held");
            deGroup.GetProperty("Id").GetString()!.Length.ShouldBeLessThanOrEqualTo(48);

            // Existing Mendix installations reject a change to an external key's length.
            using var metadata = await fixture.SendAsync("$metadata", fixture.Token());
            metadata.StatusCode.ShouldBe(HttpStatusCode.OK);
            var metadataXml = XDocument.Parse(await metadata.Content.ReadAsStringAsync(Ct));
            XNamespace edm = "http://docs.oasis-open.org/odata/ns/edm";
            var wipType = metadataXml.Descendants(edm + "EntityType")
                .Single(e => (string?)e.Attribute("Name") == "WipBoardRow");
            var key = wipType.Elements(edm + "Property").Single(e => (string?)e.Attribute("Name") == "Id");
            ((string?)key.Attribute("MaxLength")).ShouldBe("48");

            await using var original = database.CreateCommand("SELECT count(*) FROM pom.production_units;");
            (await original.ExecuteScalarAsync(Ct)).ShouldBe(2000L);

            async Task Deliver(long sequence, string stream, long version, IDomainEvent value)
            {
                var type = EventTypeName.Of(value.GetType());
                await inbox.EnqueueAsync(new StoredStreamEvent(sequence, value.SiteId, stream, version,
                    value.EventId, type.Value, 1, JsonSerializer.Serialize(value, value.GetType(), Json), "{}", Now, Now), Ct);
                await inbox.DispatchAsync(value.SiteId, cancellationToken: Ct);
            }
        }
        finally { await fixture.DisposeAsync(); }
    }
}
