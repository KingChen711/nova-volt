using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;
using Nvm.Simulator;
using Nvm.Simulator.Publishing;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.IntegrationTests;

/// <summary>M13: trace của một publish MQTT đi qua broker thật trong user property <c>traceparent</c> (MQTT 5).</summary>
public sealed class MqttTraceContextTests : IAsyncLifetime
{
    private readonly IContainer _broker = new ContainerBuilder("eclipse-mosquitto:2.0.22")
        .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
        .WithPortBinding(1883, true)
        .Build();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _broker.StartAsync(Ct);
        var port = _broker.GetMappedPublicPort(1883);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(_broker.Hostname, port, Ct);
                return;
            }
            catch (SocketException)
            { await Task.Delay(250, Ct); }
        }
        throw new TimeoutException("The mosquitto container never accepted a connection.");
    }

    public async ValueTask DisposeAsync() => await _broker.DisposeAsync();

    [Fact]
    public async Task APublishedMessage_CarriesTheTraceOfThePublishSpan_ThroughTheBroker()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "NovaVolt.Simulator",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var received = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new MqttClientFactory().CreateMqttClient();
        observer.ApplicationMessageReceivedAsync += arguments =>
        {
            var property = arguments.ApplicationMessage.UserProperties?
                .FirstOrDefault(p => p.Name == MqttTraceContext.PropertyName);
            received.TrySetResult(property is null ? null : Encoding.UTF8.GetString(property.ValueBuffer.Span));
            return Task.CompletedTask;
        };
        await observer.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_broker.Hostname, _broker.GetMappedPublicPort(1883))
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
            .WithClientId("nvm-trace-observer").Build(), Ct);
        await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic($"{SparkplugTopic.Namespace}/#").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build(), Ct);

        await using var publisher = new MqttSparkplugPublisher(new SimulatorOptions
        {
            BrokerHost = _broker.Hostname,
            BrokerPort = _broker.GetMappedPublicPort(1883),
            LinePath = "NOVAVOLT/NV1/FORMATION/F1",
            BirthDeathSequence = 1,
            ReconnectDelay = TimeSpan.FromMilliseconds(200),
        }, NullLogger<MqttSparkplugPublisher>.Instance);
        await publisher.ConnectAsync(Ct);
        var path = Nvm.Kernel.Identity.EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
        var payload = SparkplugPayload.EncodeBirth(
            [new DeviceReading(SparkplugPayload.BirthDeathSequenceMetric, null, new MetricValue.Integral(1), DateTimeOffset.UnixEpoch)],
            sequence: 0, DateTimeOffset.UnixEpoch);
        await publisher.PublishAsync(new SparkplugMessage(SparkplugTopic.For(path, SparkplugMessageType.NodeBirth), [.. payload]), Ct);

        var traceParent = await received.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        traceParent.ShouldNotBeNull("broker phải chuyển user property traceparent tới subscriber");
        MqttTraceContext.Accept(traceParent).ShouldBe(traceParent);
        var publish = spans.Single(s => s.OperationName == "mqtt publish");
        traceParent.ShouldBe(publish.Id);
        await observer.DisconnectAsync(cancellationToken: Ct);
        observer.Dispose();
    }
}
