using System.Text.Json;
using System.Text.Json.Nodes;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nvm.Bus.CloudEvents;
using Nvm.Bus.Outbox;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.Traceability;
using Nvm.EventStore;

namespace Nvm.UnitTests.Bus;

public sealed class MassTransitEventOutboxPublisherTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StoredDomainEvent_PublishesWithOriginalIdentityAndCloudEventHeaders()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<ReceivedEvent>()
            .AddMassTransitTestHarness(bus =>
            {
                bus.AddConsumer<ProbeConsumer>();
                bus.UsingInMemory((context, cfg) =>
                {
                    cfg.UseNvmCloudEvents("host-all");
                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        using var scope = provider.CreateScope();

        var now = new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);
        var domain = new ProductionUnitSerialized(Guid.NewGuid(), now, now, "NV1",
            "CELL-001", "Cell", "P-1", "WO-1", "R1", "operator-1");
        var message = Stored(domain);
        var publisher = new MassTransitEventOutboxPublisher(scope.ServiceProvider.GetRequiredService<IPublishEndpoint>());

        await publisher.PublishAsync(message, TestContext.Current.CancellationToken);
        (await harness.Consumed.Any<ProductionUnitSerialized>(TestContext.Current.CancellationToken)).ShouldBeTrue();
        var received = provider.GetRequiredService<ReceivedEvent>();
        received.MessageId.ShouldBe(domain.EventId);
        received.EventId.ShouldBe(domain.EventId);
        received.CloudEventId.ShouldBe(domain.EventId.ToString());
        received.Type.ShouldBe(message.EventType);
        received.Source.ShouldBe("urn:novavolt:nv1:app-execution");
        received.Time.ShouldBe("2026-09-23T09:00:00.0000000+00:00");
        received.Subject.ShouldBe("urn:trace-unit:cell:CELL-001");
        received.DataSchema.ShouldBe("https://example.test/schema/v1");
        received.CorrelationId.ShouldBe("WO-1");
        received.CausationId.ShouldBe("command-1");
        received.PartitionKey.ShouldBe("CELL-001");
    }

    [Fact]
    public void CorruptPayload_IsRejectedBeforePublish()
    {
        var now = new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);
        var domain = new ProductionUnitSerialized(Guid.NewGuid(), now, now,
            "NV1", "CELL-001", "Cell", "P-1", "WO-1", "R1", "operator-1");
        var message = Stored(domain) with { EventId = Guid.NewGuid() };
        Should.Throw<InvalidDataException>(() => MassTransitEventOutboxPublisher.Deserialize(message));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("type")]
    [InlineData("source")]
    [InlineData("time")]
    [InlineData("data")]
    public async Task CorruptStoredEnvelope_IsRejectedBeforePublish(string corruptAttribute)
    {
        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness(bus => bus.UsingInMemory((context, cfg) =>
            {
                cfg.UseNvmCloudEvents("host-all");
                cfg.ConfigureEndpoints(context);
            }))
            .BuildServiceProvider(true);
        var now = new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);
        var domain = new ProductionUnitSerialized(Guid.NewGuid(), now, now, "NV1",
            "CELL-001", "Cell", "P-1", "WO-1", "R1", "operator-1");
        var stored = Stored(domain);
        var envelope = JsonNode.Parse(stored.CloudEventJson)!;
        switch (corruptAttribute)
        {
            case "id":
                envelope["id"] = Guid.NewGuid().ToString();
                break;
            case "type":
                envelope["type"] = "com.novavolt.traceability.other.v1";
                break;
            case "source":
                envelope["source"] = "urn:novavolt:nv2:app-execution";
                break;
            case "time":
                envelope["time"] = now.AddMinutes(1).ToString("O");
                break;
            case "data":
                envelope["data"]!["siteId"] = "NV2";
                break;
        }

        using var scope = provider.CreateScope();
        var publisher = new MassTransitEventOutboxPublisher(scope.ServiceProvider.GetRequiredService<IPublishEndpoint>());
        await Should.ThrowAsync<InvalidDataException>(() => publisher.PublishAsync(
            stored with { CloudEventJson = envelope.ToJsonString() }, TestContext.Current.CancellationToken));
    }

    private static OutboxEvent Stored(ProductionUnitSerialized domain)
    {
        var eventType = EventTypeName.Of(typeof(ProductionUnitSerialized)).Value;
        var envelope = JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id = domain.EventId,
            type = eventType,
            source = "urn:novavolt:nv1:app-execution",
            subject = "urn:trace-unit:cell:CELL-001",
            time = domain.OccurredAt.ToString("O"),
            datacontenttype = "application/json",
            dataschema = "https://example.test/schema/v1",
            correlationid = "WO-1",
            causationid = "command-1",
            partitionkey = "CELL-001",
            data = domain
        }, Json);
        return new OutboxEvent(domain.EventId, domain.SiteId, domain.SerialNumber, 1,
            eventType, 1, JsonSerializer.Serialize(domain, Json), "{}",
            domain.OccurredAt, domain.OccurredAt, envelope);
    }

    public sealed class ReceivedEvent
    {
        public Guid? MessageId { get; set; }
        public Guid EventId { get; set; }
        public string? CloudEventId { get; set; }
        public string? Type { get; set; }
        public string? Source { get; set; }
        public string? Time { get; set; }
        public string? Subject { get; set; }
        public string? DataSchema { get; set; }
        public string? CorrelationId { get; set; }
        public string? CausationId { get; set; }
        public string? PartitionKey { get; set; }
    }

    public sealed class ProbeConsumer(ReceivedEvent received) : IConsumer<ProductionUnitSerialized>
    {
        public Task Consume(ConsumeContext<ProductionUnitSerialized> context)
        {
            received.MessageId = context.MessageId;
            received.EventId = context.Message.EventId;
            received.CloudEventId = context.Headers.Get<string>(CloudEventHeaders.Id);
            received.Type = context.Headers.Get<string>(CloudEventHeaders.Type);
            received.Source = context.Headers.Get<string>(CloudEventHeaders.Source);
            received.Time = context.Headers.Get<string>(CloudEventHeaders.Time);
            received.Subject = context.Headers.Get<string>("ce_subject");
            received.DataSchema = context.Headers.Get<string>("ce_dataschema");
            received.CorrelationId = context.Headers.Get<string>("ce_correlationid");
            received.CausationId = context.Headers.Get<string>("ce_causationid");
            received.PartitionKey = context.Headers.Get<string>("ce_partitionkey");
            return Task.CompletedTask;
        }
    }
}
