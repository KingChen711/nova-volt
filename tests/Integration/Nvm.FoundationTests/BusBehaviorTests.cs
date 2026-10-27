using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.FoundationTests.LabModel;
using RabbitMQ.Client;

namespace Nvm.FoundationTests;

/// <summary>Event lên RabbitMQ, subscription Predefined và theo plant, worker role, relay (R2).</summary>
/// <remarks>Mỗi test một plant riêng: exchange và queue mang tên plant, nên các test chạy song song không nhận event của nhau.</remarks>
public sealed class BusBehaviorTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Event_without_subscription_is_published_and_nobody_handles_it_lab_L04()
    {
        var (h, uri) = await StartAsync();
        await using var worker = new EventWorker(h.Executor, h.Rules, uri);
        await worker.StartAsync(Cancel);
        worker.Active.Select(s => s.Subscription).ShouldBe(["LabOrderAudit"]);

        var result = h.Run(new CreateLabOrder { NId = "ORD-1", MaterialNId = "MAT-1" });

        result.StatusCode.ShouldBe(200);
        // Predefined chạy; LabOrderNotify chưa được tạo ở plant này nên không ai nhận, và không có lỗi nào.
        await EventuallyAsync(() => Ticks(h, "audit:ORD-1") == 1);
        await Task.Delay(TimeSpan.FromSeconds(2), Cancel);
        Ticks(h, "notify:ORD-1").ShouldBe(0);
        var chain = h.Traces.ByCorrelation(result.CorrelationId);
        chain.ShouldContain(t => t.Message.StartsWith("Event FB_OP_Lab.LabOrderCreated v1 on the bus", StringComparison.Ordinal));
        chain.ShouldNotContain(t => t.Level == TraceLevel.Error);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.{SchemaUpdater.EventTable} WHERE PublishedOn IS NOT NULL").ShouldBe(1);
    }

    [Fact]
    public async Task Plant_subscription_runs_the_handler_after_the_response()
    {
        var (h, uri) = await StartAsync();
        h.Run(new SetEventSubscription { NId = "LabOrderNotify" }, "Foundation").StatusCode.ShouldBe(200);
        await using var worker = new EventWorker(h.Executor, h.Rules, uri);
        await worker.StartAsync(Cancel);
        worker.Active.Select(s => s.Subscription).ShouldBe(["LabOrderAudit", "LabOrderNotify"], ignoreOrder: true);

        var result = h.Run(new CreateLabOrder { NId = "ORD-2", MaterialNId = "MAT-1" });

        // Response đã trả trong khi handler còn bị giữ: handler chạy ngoài response.
        result.StatusCode.ShouldBe(200);
        Ticks(h, "notify:ORD-2").ShouldBe(0);
        LabOrderNotifier.Gate.Release();
        await EventuallyAsync(() => Ticks(h, "notify:ORD-2") == 1);
        // Handler nối vào chuỗi trace của lời gọi đã phát event.
        h.Traces.ByCorrelation(result.CorrelationId).ShouldContain(t => t.Message == "Subscription LabOrderNotify handling FB_OP_Lab.LabOrderCreated");
    }

    [Fact]
    public async Task Event_rule_calls_a_command_when_the_event_arrives()
    {
        var (h, uri) = await StartAsync();
        h.Run(new DefineSignalRule
        {
            NId = "RULE-ON-ORDER",
            Trigger = "Event",
            EventType = "FB_OP_Lab.LabOrderCreated",
            App = LabApp.Name,
            Command = nameof(RecordLabTick),
            Parameters = """{"Source":"rule:{NId}"}""",
        }, "Foundation").StatusCode.ShouldBe(200);
        await using var worker = new EventWorker(h.Executor, h.Rules, uri);
        await worker.StartAsync(Cancel);

        h.Run(new CreateLabOrder { NId = "ORD-3", MaterialNId = "MAT-1" });

        await EventuallyAsync(() => Ticks(h, "rule:ORD-3") == 1);
    }

    [Fact]
    public async Task Relay_publishes_events_left_behind_while_the_bus_was_down()
    {
        // Publisher trỏ tới một cổng không có broker: commit vẫn thành công, event nằm lại trong bảng.
        var (h, uri) = await StartAsync(brokerUri: "amqp://guest:guest@127.0.0.1:1/");

        var result = h.Run(new CreateLabOrder { NId = "ORD-4", MaterialNId = "MAT-1" });

        result.StatusCode.ShouldBe(200);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.{SchemaUpdater.EventTable} WHERE PublishedOn IS NULL").ShouldBe(1);
        h.Traces.ByCorrelation(result.CorrelationId).ShouldContain(t => t.Message.Contains("the relay will retry", StringComparison.Ordinal));

        await using var relay = new RabbitMqEventPublisher(uri, h.Solution.Plant, h.ConnectionString, h.Time, h.Traces);
        (await relay.RelayPending(TimeSpan.Zero, Cancel)).ShouldBe(1);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.{SchemaUpdater.EventTable} WHERE PublishedOn IS NULL").ShouldBe(0);
        (await relay.RelayPending(TimeSpan.Zero, Cancel)).ShouldBe(0);
    }

    [Theory]
    [InlineData(false, 3, 3)]
    [InlineData(true, 3, 1)]
    public async Task Worker_role_sets_how_many_consumers_take_events(bool sequential, int instances, int consumers)
    {
        var (h, uri) = await StartAsync(configure: s => s.WorkerRoles["Audit"] = new WorkerRoleConfiguration
        {
            Instances = instances,
            Sequential = sequential,
        });
        await using var worker = new EventWorker(h.Executor, h.Rules, uri);
        await worker.StartAsync(Cancel);

        await using var connection = await new ConnectionFactory { Uri = new Uri(uri) }.CreateConnectionAsync(Cancel);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Cancel);
        var queue = await channel.QueueDeclarePassiveAsync(BusNames.Queue(h.Solution.Plant, "Audit"), Cancel);

        queue.ConsumerCount.ShouldBe((uint)consumers);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<(FoundationHarness Harness, string Uri)> StartAsync(
        Action<SolutionConfiguration>? configure = null,
        string? brokerUri = null)
    {
        var uri = await SharedRabbitMq.UriAsync();
        var h = await FoundationHarness.CreateAsync(
            s =>
            {
                s.Plant = "T" + Guid.NewGuid().ToString("N")[..8];
                configure?.Invoke(s);
            },
            harness => new RabbitMqEventPublisher(brokerUri ?? uri, harness.Solution.Plant, harness.ConnectionString, harness.Time,
                harness.Traces));
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" }).StatusCode.ShouldBe(200);
        return (h, uri);
    }

    private static int Ticks(FoundationHarness h, string source) =>
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabTick>()}] WHERE Source = '{source}'");

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = TimeProvider.System.GetUtcNow() + Patience;
        while (!condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException("The condition did not become true in time.");
            }

            await Task.Delay(100, Cancel);
        }
    }
}
