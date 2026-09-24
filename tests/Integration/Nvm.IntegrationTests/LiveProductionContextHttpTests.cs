using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.Kernel.Commands;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;

namespace Nvm.IntegrationTests;

public sealed class LiveProductionContextHttpTests(ExecutionCommandHttpFixture fixture)
    : IClassFixture<ExecutionCommandHttpFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("NV1", false)]
    [InlineData("DE1", true)]
    public async Task LiveUnitOverridesFixture_AcceptsRunning_RejectsFinishedOrHeld_AndReplays(string site, bool hold)
    {
        // Cố ý trùng serial với fixture Running: nếu adapter đọc nhầm fixture thì rejection sẽ sai.
        var collection = ExecutionCommandHttpFixture.Request(site);
        var serial = collection.Payload.Serial;
        var token = fixture.Token(site);
        await using (var connection = new SqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            using var seed = new SqlCommand("""
                INSERT INTO traceability.Routes (SiteId, ProductCode, RoutingVersion, StepsJson, TransitionsJson)
                VALUES (@site, 'PACK-LIVE', 'r1', '[{"code":"EOL"}]',
                    '[{"action":"StartStep","from":0,"to":1},{"action":"CompleteStep","from":1,"to":2}]');
                """, connection);
            seed.Parameters.AddWithValue("@site", site);
            await seed.ExecuteNonQueryAsync(Ct);
        }
        var born = new SerializeUnitCommand(site, "operator-test", "live-birth", serial,
            collection.OccurredAt, "PACK-LIVE", "WO-LIVE", "r1");
        await TraceAsync("traceability/serialize-unit", born,
            new SerializeUnitPayload(born.SubmissionId, serial, born.ProductCode, born.WorkOrderId, born.RoutingVersion));
        var start = new StartStepCommand(site, "operator-test", "live-start", serial, collection.OccurredAt,
            "EOL", collection.Payload.OperationRunId, collection.Payload.EquipmentPath);
        await TraceAsync("production/start-step", start,
            new StartStepPayload(start.SubmissionId, serial, start.StepCode, start.OperationRunId, start.EquipmentPath));
        using var accepted = await fixture.SendAsync(collection, token);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var original = await accepted.Content.ReadAsStringAsync(Ct);
        using (var json = JsonDocument.Parse(original))
        { json.RootElement.GetProperty("accepted").GetBoolean().ShouldBeTrue(); }

        if (hold)
        {
            var duplicate = new SerializeUnitCommand(site, "operator-test", "live-duplicate", serial,
                collection.OccurredAt, "PACK-LIVE", "WO-LIVE", "r1");
            await TraceAsync("traceability/serialize-unit", duplicate,
                new SerializeUnitPayload(duplicate.SubmissionId, serial, duplicate.ProductCode, duplicate.WorkOrderId, duplicate.RoutingVersion));
        }
        else
        {
            var complete = new CompleteStepCommand(site, "operator-test", "live-complete", serial,
                collection.OccurredAt, start.StepCode, start.OperationRunId);
            await TraceAsync("production/complete-step", complete,
                new CompleteStepPayload(complete.SubmissionId, serial, complete.StepCode, complete.OperationRunId));
        }

        var submission = Guid.NewGuid().ToString();
        var next = collection with
        {
            IdempotencyKey = IdempotencyKey.FromNaturalKey(site, "RecordDataCollection", submission).Value,
            Payload = collection.Payload with { SubmissionId = submission }
        };
        using var rejected = await fixture.SendAsync(next, token);
        rejected.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var rejection = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(Ct));
        rejection.RootElement.GetProperty("accepted").GetBoolean().ShouldBeFalse();
        rejection.RootElement.GetProperty("reasonCode").GetString().ShouldBe(hold ? "QUALITY_HOLD" : "OPERATION_NOT_RUNNING");
        (await fixture.StoredRecordAsync(next)).ShouldBeNull();
        using var replay = await fixture.SendAsync(collection, token);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(original);
        (await fixture.OutcomeCountAsync(collection)).ShouldBe(1);
        (await fixture.EventsForAsync(collection)).Length.ShouldBe(1);
        using var wrongSite = await fixture.SendAsync(collection, fixture.Token(site == "NV1" ? "DE1" : "NV1"));
        wrongSite.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        async Task TraceAsync<T>(string route, UnitCommand command, T payload)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/commands/" + route)
            {
                Content = JsonContent.Create(new TraceabilityRequest<T>(command.IdempotencyKey.Value.ToString(),
                    site, command.OccurredAt, payload))
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await fixture.Client.SendAsync(request, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        }
    }
}
