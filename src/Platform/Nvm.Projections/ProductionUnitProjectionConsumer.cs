using System.Text.Json;
using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Quality;
using Nvm.Contracts.Events.Traceability;

namespace Nvm.Projections;

/// <summary>Broker acknowledgement occurs only after the projection inbox commits.</summary>
[BusEndpoint("traceability", "unit-projection")]
public sealed class ProductionUnitProjectionConsumer(SqlGlobalEventFeed source, ProductionUnitProjectionInbox inbox)
    : IConsumer<ProductionUnitSerialized>, IConsumer<ProcessStepStarted>,
        IConsumer<ProcessStepCompleted>, IConsumer<UnitMeasurementRecorded>, IConsumer<DuplicateSerialDetected>,
        IConsumer<UnitQuarantined>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task Consume(ConsumeContext<ProductionUnitSerialized> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<ProcessStepStarted> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<ProcessStepCompleted> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<UnitMeasurementRecorded> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<DuplicateSerialDetected> context) => CaptureAsync(context);
    public Task Consume(ConsumeContext<UnitQuarantined> context) => CaptureAsync(context);

    private async Task CaptureAsync<T>(ConsumeContext<T> context) where T : class, IDomainEvent
    {
        var message = context.Message;
        var fact = await source.FindAsync(message.SiteId, message.EventId, context.CancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Projection event is absent from its committed source.");
        if (fact.EventType != EventTypeName.Of(typeof(T)).Value || fact.SchemaVersion != 1)
        { throw new InvalidDataException("Broker event contract differs from the committed source."); }
        var stored = JsonSerializer.Deserialize<T>(fact.PayloadJson, Json);
        if (!EqualityComparer<T>.Default.Equals(stored, message))
        { throw new InvalidDataException("Broker event differs from the committed source payload."); }
        await inbox.EnqueueAsync(fact, context.CancellationToken).ConfigureAwait(false);
    }
}
