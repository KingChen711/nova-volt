using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Nvm.Foundation.Runtime;

/// <summary>Tên trên bus của một plant: một exchange topic, mỗi worker role một queue, routing key là tên event.</summary>
public static class BusNames
{
    /// <summary>Worker role nhận event cho signal rule theo event.</summary>
    public const string SignalRuleRole = "SignalRules";

    public static string Exchange(string plant) => $"nvm.{plant}.events";

    public static string Queue(string plant, string workerRole) => $"nvm.{plant}.{workerRole}";
}

/// <summary>Phát event lên RabbitMQ sau commit; dòng nào chưa phát được thì <see cref="RelayPending"/> gửi lại.</summary>
/// <remarks>
/// Event đã nằm trong bảng event cùng transaction với dữ liệu. Lỗi bus không làm command thất bại: dòng giữ
/// <c>PublishedOn</c> rỗng và relay gửi lại. Bus nhận event mà không queue nào bind thì event mất, không lỗi (lab L04).
/// </remarks>
public sealed class RabbitMqEventPublisher(
    string rabbitUri,
    string plant,
    string sqlConnectionString,
    TimeProvider time,
    ITraceSink traces) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public void Publish(IReadOnlyList<PublishedEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        try
        {
            PublishAsync(events.Select(e => new Row(e.EventId, e.EventType, e.Version, e.Payload, e.CorrelationId)).ToList())
                .GetAwaiter().GetResult();
        }
#pragma warning disable CA1031 // command đã commit; bus chết thì relay gửi lại, không báo lỗi cho người gọi
        catch (Exception e)
#pragma warning restore CA1031
        {
            foreach (var row in events)
            {
                Trace(row.CorrelationId, TraceLevel.Warning, $"Event {row.EventType} not on the bus yet ({e.GetType().Name}); the relay will retry");
            }
        }
    }

    /// <summary>Gửi lại event chưa phát và cũ hơn <paramref name="olderThan"/>. Trả số event đã gửi.</summary>
    public async Task<int> RelayPending(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var rows = new List<Row>();
        await using (var connection = new SqlConnection(sqlConnectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT TOP 500 EventId, EventType, EventVersion, Payload, CorrelationId
                FROM dbo.{SchemaUpdater.EventTable}
                WHERE PublishedOn IS NULL AND RaisedOn <= @cutoff
                ORDER BY Id;
                """;
            command.Parameters.AddWithValue("@cutoff", time.GetUtcNow() - olderThan);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new Row(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetGuid(4)));
            }
        }

        if (rows.Count > 0)
        {
            await PublishAsync(rows, cancellationToken).ConfigureAwait(false);
        }

        return rows.Count;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task PublishAsync(IReadOnlyList<Row> rows, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var channel = await ChannelAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                var properties = new BasicProperties
                {
                    Type = row.EventType,
                    MessageId = row.EventId.ToString(),
                    CorrelationId = row.CorrelationId.ToString(),
                    ContentType = "application/json",
                    Persistent = true,
                    Headers = new Dictionary<string, object?> { ["nvm-version"] = row.Version },
                };

                // Publisher confirm: hàm chỉ trả về khi broker đã nhận.
                await channel.BasicPublishAsync(BusNames.Exchange(plant), row.EventType, mandatory: false, properties,
                    Encoding.UTF8.GetBytes(row.Payload), cancellationToken).ConfigureAwait(false);
                Trace(row.CorrelationId, TraceLevel.Information, $"Event {row.EventType} v{row.Version} on the bus ({BusNames.Exchange(plant)})");
            }
        }
        finally
        {
            _gate.Release();
        }

        await MarkPublishedAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        _connection ??= await new ConnectionFactory { Uri = new Uri(rabbitUri) }.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);
        await _channel.ExchangeDeclareAsync(BusNames.Exchange(plant), ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return _channel;
    }

    private async Task MarkPublishedAsync(IReadOnlyList<Row> rows, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(sqlConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE dbo.{SchemaUpdater.EventTable} SET PublishedOn = @now WHERE EventId = @id AND PublishedOn IS NULL;";
            command.Parameters.AddWithValue("@now", time.GetUtcNow());
            command.Parameters.AddWithValue("@id", row.EventId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Trace(Guid correlationId, TraceLevel level, string message) =>
        traces.Write(new TraceRecord(time.GetUtcNow(), CallTracer.PlatformProvider, level, correlationId, Guid.NewGuid(), null,
            correlationId, "Bus", message));

    private sealed record Row(Guid EventId, string EventType, int Version, string Payload, Guid CorrelationId);
}

/// <summary>
/// Worker nhận event: mỗi worker role một queue, bind theo subscription đang bật lúc khởi động. Subscription Predefined
/// luôn bật; subscription theo plant bật bằng dữ liệu (<see cref="EventSubscription"/>), có hiệu lực khi worker khởi động lại.
/// </summary>
public sealed class EventWorker(CommandExecutor executor, SignalRuleEngine rules, string rabbitUri) : IAsyncDisposable
{
    private readonly List<IChannel> _channels = [];
    private IConnection? _connection;

    /// <summary>Subscription đang nhận event, sau lần khởi động gần nhất.</summary>
    public IReadOnlyList<EventSubscriberModel> Active { get; private set; } = [];

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var registry = executor.Registry;
        var plant = executor.Solution.Plant;
        HashSet<string> enabled;
        List<string> ruleEvents;
        using (var read = executor.OpenRead())
        {
            enabled = [.. read.SetOf<EventSubscription>().Where(s => s.Enabled).Select(s => s.NId)];
            ruleEvents = [.. read.SetOf<SignalRule>().Where(r => r.Enabled && r.Trigger == "Event" && r.EventType != null)
                .Select(r => r.EventType!).Distinct()];
        }

        Active = [.. registry.EventSubscribers.Where(s => s.Predefined || enabled.Contains(s.Subscription))];

        _connection = await new ConnectionFactory { Uri = new Uri(rabbitUri) }.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var setup = await _connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await setup.ExchangeDeclareAsync(BusNames.Exchange(plant), ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var roles = registry.EventSubscribers.Select(s => s.WorkerRole).Append(BusNames.SignalRuleRole).Distinct(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            var subscribers = Active.Where(s => string.Equals(s.WorkerRole, role, StringComparison.Ordinal)).ToList();
            var wanted = role == BusNames.SignalRuleRole
                ? ruleEvents.ToHashSet(StringComparer.Ordinal)
                : subscribers.Select(s => registry.EventOf(s.EventType).Name).ToHashSet(StringComparer.Ordinal);
            var queue = BusNames.Queue(plant, role);
            await setup.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Binding cũ của subscription đã tắt phải gỡ, không thì queue vẫn nhận event không ai xử lý.
            foreach (var eventModel in registry.Events)
            {
                if (wanted.Contains(eventModel.Name))
                {
                    await setup.QueueBindAsync(queue, BusNames.Exchange(plant), eventModel.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await setup.QueueUnbindAsync(queue, BusNames.Exchange(plant), eventModel.Name, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }

            if (wanted.Count == 0)
            {
                continue;
            }

            var configuration = executor.Solution.WorkerRole(role);
            var instances = configuration.Sequential ? 1 : Math.Max(1, configuration.Instances);
            for (var i = 0; i < instances; i++)
            {
                var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await channel.BasicQosAsync(0, 1, global: false, cancellationToken).ConfigureAwait(false);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    await DispatchAsync(role, subscribers, delivery).ConfigureAwait(false);
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false).ConfigureAwait(false);
                };
                await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken).ConfigureAwait(false);
                _channels.Add(channel);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _channels)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(string role, IReadOnlyList<EventSubscriberModel> subscribers, BasicDeliverEventArgs delivery)
    {
        var eventType = delivery.BasicProperties.Type;
        var payload = Encoding.UTF8.GetString(delivery.Body.Span);
        var correlationId = Guid.TryParse(delivery.BasicProperties.CorrelationId, out var id) ? id : Guid.NewGuid();
        if (eventType is null || executor.Registry.EventByName(eventType) is not { } model)
        {
            return;
        }

        if (role == BusNames.SignalRuleRole)
        {
            await Task.Run(() => rules.RunEventRules(eventType, payload, correlationId)).ConfigureAwait(false);
            return;
        }

        var domainEvent = (IFoundationEvent)JsonSerializer.Deserialize(payload, model.Type)!;
        foreach (var subscriber in subscribers.Where(s => s.EventType == model.Type))
        {
            await Task.Run(() => executor.RunEventSubscriber(subscriber, domainEvent, correlationId)).ConfigureAwait(false);
        }
    }
}
