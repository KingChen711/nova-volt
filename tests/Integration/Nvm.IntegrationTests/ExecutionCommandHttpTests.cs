using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Nvm.App.Execution;

namespace Nvm.IntegrationTests;

public sealed class ExecutionCommandHttpTests(ExecutionCommandHttpFixture fixture) : IClassFixture<ExecutionCommandHttpFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("event")]
    [InlineData("stream")]
    [InlineData("ack")]
    [InlineData("route")]
    [InlineData("incident")]
    public async Task MissingRuntimeGrant_IsNotReady_AndRecoversWhenRestored(string scenario)
    {
        using var baseline = await fixture.Client.GetAsync("/health/ready", Ct);
        baseline.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        using var permissionChange = new SqlCommand("""
            IF @revoke = 1
            BEGIN
                IF @scenario = 'event' REVOKE INSERT ON es.Events FROM nvm_app;
                ELSE IF @scenario = 'stream' REVOKE UPDATE (Version) ON es.Streams FROM nvm_app;
                ELSE IF @scenario = 'ack' REVOKE UPDATE (DispatchedAt) ON es.Outbox FROM nvm_app;
                ELSE IF @scenario = 'route' REVOKE SELECT ON traceability.Routes FROM nvm_app;
                ELSE IF @scenario = 'incident' REVOKE INSERT ON traceability.DuplicateSerialIncidents FROM nvm_app;
                ELSE THROW 51000, 'Unknown permission scenario', 1;
            END
            ELSE
            BEGIN
                IF @scenario = 'event' GRANT INSERT ON es.Events TO nvm_app;
                ELSE IF @scenario = 'stream' GRANT UPDATE (Version) ON es.Streams TO nvm_app;
                ELSE IF @scenario = 'ack' GRANT UPDATE (DispatchedAt) ON es.Outbox TO nvm_app;
                ELSE IF @scenario = 'route' GRANT SELECT ON traceability.Routes TO nvm_app;
                ELSE IF @scenario = 'incident' GRANT INSERT ON traceability.DuplicateSerialIncidents TO nvm_app;
                ELSE THROW 51000, 'Unknown permission scenario', 1;
            END
            """, connection);
        permissionChange.Parameters.AddWithValue("scenario", scenario);
        permissionChange.Parameters.AddWithValue("revoke", true);
        try
        {
            await permissionChange.ExecuteNonQueryAsync(Ct);
            using var unavailable = await fixture.Client.GetAsync("/health/ready", Ct);
            unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            permissionChange.Parameters["revoke"].Value = false;
            await permissionChange.ExecuteNonQueryAsync(Ct);
        }
        using var recovered = await fixture.Client.GetAsync("/health/ready", Ct);
        recovered.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TwoIndependentProcessesRaceOnANewSubmission_WriteAndPublishOnce()
    {
        await using var peer = await fixture.StartPeerAsync();
        peer.ProcessId.ShouldNotBe(fixture.ProcessId);
        var request = ExecutionCommandHttpFixture.Request();
        var token = fixture.Token();
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            ExecutionCommandHttpFixture.SendToAsync(i % 2 == 0 ? fixture.Client : peer.Client, request, token)));
        var expected = await replies[0].Content.ReadAsStringAsync(Ct);
        foreach (var reply in replies)
        {
            using (reply)
            {
                reply.StatusCode.ShouldBe(HttpStatusCode.OK);
                (await reply.Content.ReadAsStringAsync(Ct)).ShouldBe(expected);
            }
        }
        (await fixture.OutcomeCountAsync(request)).ShouldBe(1);
        (await fixture.StoredRecordAsync(request)).ShouldNotBeNull();
        (await fixture.EventsForAsync(request)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task ResponseLostAfterCommit_ReplayAfterProcessRestartReturnsStoredOutcome()
    {
        var request = ExecutionCommandHttpFixture.Request();
        var token = fixture.Token();
        var committed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var proxy = builder.Build();
        proxy.Urls.Add("http://127.0.0.1:0");
        proxy.MapPost("/api/v1/commands/production/record-data-collection", async (HttpContext context) =>
        {
            // Proxy nhận ACK của backend rồi cắt TCP trước khi chuyển ACK cho caller.
            using var reply = await fixture.SendAsync(request, token);
            reply.StatusCode.ShouldBe(HttpStatusCode.OK);
            committed.SetResult(await reply.Content.ReadAsStringAsync(Ct));
            context.Abort();
        });
        await proxy.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(proxy.Urls.Single()) };
        await Should.ThrowAsync<HttpRequestException>(() => ExecutionCommandHttpFixture.SendToAsync(client, request, token));
        var original = await committed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await fixture.StoredRecordAsync(request)).ShouldNotBeNull();
        await fixture.RestartAppAsync();
        using var replay = await fixture.SendAsync(request, token);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(original);
        (await fixture.OutcomeCountAsync(request)).ShouldBe(1);
        (await fixture.EventsForAsync(request)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task AuthenticatedCollection_IsAcceptedOverRealHttp()
    {
        using var response = await fixture.SendAsync(ExecutionCommandHttpFixture.Request(), fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("accepted").GetBoolean().ShouldBeTrue();
        foreach (var name in new[] { "reasonCode", "reasonText", "blockingRules", "allowedNextActions", "correlationId" })
        {
            json.RootElement.TryGetProperty(name, out _).ShouldBeTrue(name);
        }
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("audience", HttpStatusCode.Unauthorized)]
    [InlineData("role", HttpStatusCode.Forbidden)]
    [InlineData("site", HttpStatusCode.Forbidden)]
    [InlineData("multiple-sites", HttpStatusCode.Forbidden)]
    [InlineData("empty-actor", HttpStatusCode.Forbidden)]
    [InlineData("long-actor", HttpStatusCode.Forbidden)]
    public async Task InvalidPrincipal_CannotSubmit(string scenario, HttpStatusCode expected)
    {
        var token = scenario switch
        {
            "anonymous" => null,
            "expired" => fixture.Token(expired: true),
            "audience" => fixture.Token(wrongAudience: true),
            "role" => fixture.Token(role: "Supervisor"),
            "site" => fixture.Token(site: "XX1"),
            "empty-actor" => fixture.Token(actor: " "),
            "long-actor" => fixture.Token(actor: new string('a', 201)),
            _ => fixture.Token(secondSite: "DE1"),
        };
        var request = ExecutionCommandHttpFixture.Request();
        using var response = await fixture.SendAsync(request, token);
        response.StatusCode.ShouldBe(expected);
        (await fixture.OutcomeCountAsync(request)).ShouldBe(0);
    }

    [Fact]
    public async Task PrincipalCannotChooseOtherSiteInBody()
    {
        using var response = await fixture.SendAsync(ExecutionCommandHttpFixture.Request("DE1"), fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("NV1", "Operator")]
    [InlineData("DE1", "LineLeader")]
    public async Task BothRolesAndSites_CanRecordInOwnSite(string site, string role)
    {
        using var response = await fixture.SendAsync(ExecutionCommandHttpFixture.Request(site), fixture.Token(site, role));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("accepted").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task SequentialConcurrentAndProcessRestart_ReplayIdenticalOutcome()
    {
        var request = ExecutionCommandHttpFixture.Request();
        var token = fixture.Token();
        using var first = await fixture.SendAsync(request, token);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var expected = await first.Content.ReadAsStringAsync(Ct);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => fixture.SendAsync(request, token)));
        foreach (var response in concurrent)
        {
            using (response)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                (await response.Content.ReadAsStringAsync(Ct)).ShouldBe(expected);
            }
        }
        var previousPid = fixture.ProcessId;
        await fixture.RestartAppAsync();
        fixture.ProcessId.ShouldNotBe(previousPid);
        using var replay = await fixture.SendAsync(request, token);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(expected);
        (await fixture.OutcomeCountAsync(request)).ShouldBe(1);
        (await fixture.StoredRecordAsync(request)).ShouldNotBeNull();
        (await fixture.EventsForAsync(request)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task OmittedClientKeyReplaysTheSameOutcomeAfterRestart()
    {
        var request = ExecutionCommandHttpFixture.Request();
        var withoutKey = new { request.SiteId, request.OccurredAt, request.Payload };
        var token = fixture.Token();
        using var first = await fixture.SendAsync(withoutKey, token);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var expected = await first.Content.ReadAsStringAsync(Ct);
        await fixture.RestartAppAsync();
        using var replay = await fixture.SendAsync(withoutKey, token);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(expected);
        (await fixture.OutcomeCountAsync(request)).ShouldBe(1);
        (await fixture.EventsForAsync(request)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task SameIdentityCannotChangeFrozenPayloadOrActor()
    {
        var request = ExecutionCommandHttpFixture.Request();
        using var accepted = await fixture.SendAsync(request, fixture.Token());
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var conflicting in new[]
        {
            request with { Payload = request.Payload with { Value = request.Payload.Value + 1 } },
            request with { OccurredAt = request.OccurredAt.AddMinutes(1) },
        })
        {
            using var response = await fixture.SendAsync(conflicting, fixture.Token());
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        using var otherActor = await fixture.SendAsync(request, fixture.Token(actor: "someone-else"));
        otherActor.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task InvalidShapeDoesNotConsumeSubmission()
    {
        var request = ExecutionCommandHttpFixture.Request();
        using var malformed = await fixture.SendAsync(request with { IdempotencyKey = Guid.NewGuid() }, fixture.Token());
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.OutcomeCountAsync(request)).ShouldBe(0);
        using var retry = await fixture.SendAsync(request, fixture.Token());
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Held")]
    [InlineData("Scrapped")]
    [InlineData("Scheduled")]
    [InlineData("Completed")]
    public async Task BlockedContext_PersistsSameRejectionAfterRestart(string state)
    {
        var unit = OperatorFixture.GenerateUnits().First(u => u.SiteId == "NV1" && u.StepCode == "EOL"
            && (u.QualityState == state || u.ExecutionState == state));
        var request = ExecutionCommandHttpFixture.Request(serial: unit.SerialNumber);
        using var first = await fixture.SendAsync(request, fixture.Token());
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var expected = await first.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(expected);
        body.RootElement.GetProperty("accepted").GetBoolean().ShouldBeFalse();
        (await fixture.StoredRecordAsync(request)).ShouldBeNull();
        (await fixture.OutcomeCountAsync(request)).ShouldBe(1);
        body.RootElement.GetProperty("reasonText").GetString().ShouldNotBeNullOrWhiteSpace();
        expected.ShouldNotContain("Override");
        (await fixture.EventsForAsync(request)).ShouldBeEmpty();
        await fixture.RestartAppAsync();
        using var replay = await fixture.SendAsync(request, fixture.Token());
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("equipment")]
    [InlineData("step")]
    [InlineData("cell")]
    [InlineData("unknown-unit")]
    public async Task ContextMismatch_DoesNotAccept(string mismatch)
    {
        var request = ExecutionCommandHttpFixture.Request();
        var payload = request.Payload;
        payload = mismatch switch
        {
            "operation" => payload with { OperationRunId = "OPRUN-NV1-EOL-9999" },
            "equipment" => payload with { EquipmentPath = "NOVAVOLT/NV1/PACK/P1/PLOAD-01" },
            "step" => payload with { StepCode = "PLOAD" },
            "cell" => payload with { Serial = OperatorFixture.GenerateUnits().First(u => u.SiteId == "NV1" && u.UnitKind == "Cell").SerialNumber },
            _ => payload with { Serial = "NV1PP16250A99999" },
        };
        using var response = await fixture.SendAsync(request with { Payload = payload }, fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("accepted").GetBoolean().ShouldBeFalse();
        (await fixture.StoredRecordAsync(request)).ShouldBeNull();
        (await fixture.OutcomeCountAsync(request)).ShouldBe(1);
        if (mismatch == "step")
        { body.RootElement.GetProperty("blockingRules")[0].GetProperty("actual").GetString().ShouldBe("PLOAD"); }
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("signal")]
    [InlineData("serial")]
    [InlineData("submission")]
    [InlineData("long-operation")]
    public async Task MalformedInput_IsRejectedBeforeClaim(string mismatch)
    {
        var request = ExecutionCommandHttpFixture.Request();
        var payload = mismatch switch
        {
            "unit" => request.Payload with { UnitOfMeasure = "mV" },
            "signal" => request.Payload with { SignalCode = "SomethingElse" },
            "serial" => request.Payload with { Serial = request.Payload.Serial.ToLowerInvariant() },
            "long-operation" => request.Payload with { OperationRunId = new string('a', 101) },
            _ => request.Payload with { SubmissionId = "not-a-uuid" },
        };
        using var bad = await fixture.SendAsync(request with { Payload = payload }, fixture.Token());
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var good = await fixture.SendAsync(request, fixture.Token());
        good.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await good.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("accepted").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task CanonicalPayload_IgnoresEquivalentDecimalScaleAndTimeOffset()
    {
        var request = ExecutionCommandHttpFixture.Request();
        using var first = await fixture.SendAsync(request, fixture.Token());
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var equivalent = request with { OccurredAt = request.OccurredAt.ToOffset(TimeSpan.FromHours(7)), Payload = request.Payload with { Value = 401.25m } };
        using var replay = await fixture.SendAsync(equivalent, fixture.Token());
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task EventCarriesSameIdentityActorValueAndSeparateTimestamps()
    {
        var request = ExecutionCommandHttpFixture.Request();
        var before = TimeProvider.System.GetUtcNow();
        using var accepted = await fixture.SendAsync(request, fixture.Token(actor: "event-operator"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var events = await fixture.EventsForAsync(request);
        events.Length.ShouldBe(1);
        var message = events[0];
        message.GetProperty("routing_key").GetString().ShouldBe("nvm.NV1.production-execution.data-collection-recorded.v1");
        var headers = message.GetProperty("properties").GetProperty("headers");
        headers.GetProperty("ce_type").GetString().ShouldBe("com.novavolt.production-execution.data-collection-recorded.v1");
        var envelope = await fixture.StoredEnvelopeAsync(request);
        foreach (var name in new[] { "id", "source", "type", "subject", "correlationid", "causationid", "partitionkey" })
        {
            headers.GetProperty("ce_" + name).GetString().ShouldBe(envelope.GetProperty(name).GetString());
        }
        using var wire = JsonDocument.Parse(message.GetProperty("payload").GetString()!);
        var data = wire.RootElement.GetProperty("message");
        data.GetProperty("eventId").GetGuid().ShouldBe(request.IdempotencyKey);
        data.GetProperty("siteId").GetString().ShouldBe("NV1");
        data.GetProperty("actorId").GetString().ShouldBe("event-operator");
        data.GetProperty("value").GetDecimal().ShouldBe(request.Payload.Value);
        data.GetProperty("occurredAt").GetDateTimeOffset().ShouldBe(request.OccurredAt);
        data.GetProperty("recordedAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(before);
        var stored = (await fixture.StoredRecordAsync(request))!.Value;
        stored.GetProperty("ActorId").GetString().ShouldBe("event-operator");
        stored.GetProperty("IdempotencyKey").GetGuid().ShouldBe(request.IdempotencyKey);
        stored.GetProperty("RecordedAt").GetDateTimeOffset().ShouldBe(data.GetProperty("recordedAt").GetDateTimeOffset());
        stored.GetProperty("OccurredAt").GetDateTimeOffset().ShouldBe(request.OccurredAt);
    }

    [Fact]
    public async Task FullDecimalIsPreservedAndCollectionDoesNotChangeUnitState()
    {
        var request = ExecutionCommandHttpFixture.Request();
        request = request with { Payload = request.Payload with { Value = decimal.MaxValue } };
        var context = await fixture.ContextJsonAsync(request);
        using var accepted = await fixture.SendAsync(request, fixture.Token());
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = (await fixture.StoredRecordAsync(request))!.Value;
        decimal.Parse(stored.GetProperty("Value").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(decimal.MaxValue);
        (await fixture.ContextJsonAsync(request)).ShouldBe(context);
        var events = await fixture.EventsForAsync(request);
        events.Length.ShouldBe(1);
        using var wire = JsonDocument.Parse(events[0].GetProperty("payload").GetString()!);
        wire.RootElement.GetProperty("message").GetProperty("value").GetDecimal().ShouldBe(decimal.MaxValue);
    }

    [Fact]
    public async Task AcceptedReplayDoesNotReevaluateLaterHoldOrPublishAgain()
    {
        var request = ExecutionCommandHttpFixture.Request();
        using var first = await fixture.SendAsync(request, fixture.Token());
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.EventsForAsync(request)).Length.ShouldBe(1);
        var original = await fixture.ContextJsonAsync(request);
        try
        {
            var held = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            held["QualityState"] = "Held";
            await fixture.ContextJsonAsync(request, held.ToJsonString());
            using var replay = await fixture.SendAsync(request, fixture.Token());
            (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
            (await fixture.EventsForAsync(request)).ShouldBeEmpty();
        }
        finally { await fixture.ContextJsonAsync(request, original); }
    }

    [Fact]
    public async Task BrokerFailureAfterCommitAndProcessRestartDeliverDurableEvent()
    {
        var request = ExecutionCommandHttpFixture.Request();
        try
        {
            await fixture.StopBrokerAsync();
            using var response = await fixture.SendAsync(request, fixture.Token());
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var original = await response.Content.ReadAsStringAsync(Ct);
            using var json = JsonDocument.Parse(original);
            json.RootElement.GetProperty("accepted").GetBoolean().ShouldBeTrue();
            (await fixture.StoredRecordAsync(request)).ShouldNotBeNull();
            (await fixture.OutcomeCountAsync(request)).ShouldBe(1);
            (await fixture.EventIntentCountsAsync(request)).ShouldBe([1, 1]);
            await fixture.StopAppAsync();
            await fixture.StartBrokerAsync();
            await fixture.StartAppAsync();
            using var replay = await fixture.SendAsync(request, fixture.Token());
            (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(original);
            (await fixture.EventsForAsync(request, timeoutSeconds: 150)).Length.ShouldBe(1);
            (await fixture.EventsForAsync(request)).ShouldBeEmpty();
        }
        finally { await fixture.StartBrokerAsync(); }
    }

    [Fact]
    public async Task OutcomeWriteFailureRollsBackCollectionAndDoesNotPublishPendingEvent()
    {
        var request = ExecutionCommandHttpFixture.Request();
        try
        {
            await fixture.FailOutcomeUpdateAsync(true);
            using var failed = await fixture.SendAsync(request, fixture.Token());
            failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await failed.Content.ReadAsStringAsync(Ct)).ShouldContain("RetrySameSubmission");
            (await fixture.StoredRecordAsync(request)).ShouldBeNull();
            (await fixture.OutcomeCountAsync(request)).ShouldBe(0);
            (await fixture.EventsForAsync(request)).ShouldBeEmpty();
            (await fixture.EventIntentCountsAsync(request)).ShouldBe([0, 0]);
        }
        finally { await fixture.FailOutcomeUpdateAsync(false); }
        using var retry = await fixture.SendAsync(request, fixture.Token());
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixture.StoredRecordAsync(request)).ShouldNotBeNull();
        (await fixture.EventsForAsync(request)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task RuntimeCanAppendButCannotRewriteReceivedCollection()
        => (await fixture.CollectionPermissionsAsync()).ShouldBe([1, 1, 0, 0]);

    [Theory]
    [InlineData("value")]
    [InlineData("payload")]
    [InlineData("occurredAt")]
    public async Task MissingRequiredInputCannotBecomeAnImplicitDefault(string missing)
    {
        var request = ExecutionCommandHttpFixture.Request();
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(request, JsonOptions))!;
        if (missing == "value")
        { json["payload"]!.AsObject().Remove("value"); }
        else
        { json.AsObject().Remove(missing); }
        using var response = await fixture.SendAsync(json, fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.OutcomeCountAsync(request)).ShouldBe(0);
    }

    [Fact]
    public async Task ForeignSerialDoesNotRevealOtherSiteContext()
    {
        var request = ExecutionCommandHttpFixture.Request();
        var foreign = ExecutionCommandHttpFixture.Request("DE1");
        using var response = await fixture.SendAsync(request with { Payload = request.Payload with { Serial = foreign.Payload.Serial } }, fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("accepted").GetBoolean().ShouldBeFalse();
        json.RootElement.GetProperty("reasonCode").GetString().ShouldBe("UNIT_NOT_FOUND");
        (await fixture.StoredRecordAsync(request)).ShouldBeNull();
    }
}
