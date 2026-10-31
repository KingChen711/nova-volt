using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Integration;
using Nvm.Product;
using Nvm.Project;
using Testcontainers.Redpanda;

namespace Nvm.SolutionTests;

/// <summary>
/// R11: gateway và hệ giả lập chạy thật trên cổng cục bộ, MOM là executor của test. Mỗi luồng khứ hồi; hệ của khách chết khi gửi đồng bộ thì MOM không
/// ghi nửa vời; ngày bị chặn thì tin vào hàng đợi rồi đi khi hết chặn (lab L21).
/// </summary>
public sealed class GatewayTests
{
    private const string ApiKey = "test-key";

    private sealed class ExecutorMom(SolutionHarness h) : IMomClient
    {
        public Task<MomReply> CallAsync(string app, string command, string body, CancellationToken cancel)
        {
            using var document = JsonDocument.Parse(body);
            var result = h.Executor.Execute(app, command, document.RootElement.Clone(), CallerIdentity.System);
            return Task.FromResult(new MomReply(result.StatusCode, result.Body.ToJsonString()));
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly WebApplication _gateway;
        private readonly WebApplication _external;
        private readonly HttpClient _http = new();

        private Rig(SolutionHarness h, ExternalSystems systems, WebApplication gateway, WebApplication external, string gatewayUrl, string folder)
        {
            H = h;
            Systems = systems;
            _gateway = gateway;
            _external = external;
            GatewayUrl = gatewayUrl;
            Folder = folder;
        }

        public SolutionHarness H { get; }

        public ExternalSystems Systems { get; }

        public string GatewayUrl { get; }

        public string Folder { get; }

        public static async Task<Rig> StartAsync(SolutionHarness h, string? kafka = null)
        {
            var systems = new ExternalSystems();
            var externalUrl = $"http://localhost:{FreePort()}";
            var gatewayUrl = $"http://localhost:{FreePort()}";
            var folder = Directory.CreateTempSubdirectory("nvm-gateway-").FullName;
            var config = new GatewayConfig(
                [
                    new("ErpProduction", "WebClient", externalUrl + "/erp/production"),
                    new("ErpConsumption", "WebClient", externalUrl + "/erp/consumption"),
                    new("ErpAck", "WebClient", externalUrl + "/erp/ack"),
                    new("SupplierPortal", "WebClient", externalUrl + "/supplier/part-address"),
                    new("PartsSupplier", "WebClient", externalUrl + "/supplier/parts"),
                    new(ShipAdapters.SkidBuild, "WebClient", externalUrl + "/customer/skid-build"),
                    new(ShipAdapters.ShipLoad, "WebClient", externalUrl + "/customer/ship-load"),
                    new("WMS", "WebClient", externalUrl + "/wms/picklist"),
                    new("QualityAck", "WebClient", externalUrl + "/quality/ack"),
                    new("ScrapFile", "File", Directory: Path.Combine(folder, "scrap")),
                    new("ReorderSvc", "File", Directory: Path.Combine(folder, "reorder")),
                    new("SupplierAck", "Kafka", Topic: "nv.supplier.ack"),
                ],
                [
                    new("direct", "File", "DirectDelivery", Directory: Path.Combine(folder, "in")),
                    .. kafka is null ? Array.Empty<InAdapter>() : [new InAdapter("schedules", "Kafka", "DeliverySchedule", Topic: "nv.supplier.schedules", AckAdapter: "SupplierAck")],
                ],
                [
                    new("SalesOrder", ShipApp.Name, nameof(ReceiveSalesOrders), AckAdapter: "ErpAck"),
                    new("DeliverySchedule", YardApp.Name, nameof(ReceiveDeliverySchedule)),
                    new("Containment", ProductionApp.Name, nameof(ImportContainmentRequest), PayloadField: null, AckAdapter: "QualityAck"),
                    new("DirectDelivery", PullApp.Name, nameof(ReceiveDirectDelivery)),
                    new("PicklistStatus", PullApp.Name, nameof(UpdateTaskFromWms), PayloadField: null),
                ],
                ApiKey,
                kafka,
                FilePoll: TimeSpan.FromMilliseconds(200));
            var external = systems.Build(externalUrl);
            var gateway = GatewayApp.Build(config, new ExecutorMom(h), gatewayUrl);
            await external.StartAsync(TestContext.Current.CancellationToken);
            await gateway.StartAsync(TestContext.Current.CancellationToken);

            var routing = new RoutingMessageGateway();
            foreach (var adapter in config.Out.Select(a => a.Name))
            {
                routing.Adapters[adapter] = new HttpMessageAdapter(new Uri(gatewayUrl + "/"), new HttpClient());
            }

            h.Executor.Gateway = routing;
            return new Rig(h, systems, gateway, external, gatewayUrl, folder);
        }

        public async Task<HttpResponseMessage> PostInAsync(string type, string body, string? key = ApiKey)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GatewayUrl}/api/in/{type}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (key is not null)
            {
                request.Headers.Add("X-Api-Key", key);
            }

            return await _http.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _gateway.StopAsync();
            await _external.StopAsync();
            await _gateway.DisposeAsync();
            await _external.DisposeAsync();
            _http.Dispose();
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private static void Ok(CommandResult result) => result.StatusCode.ShouldBe(200, Fg.Message(result));

    private static async Task Until(Func<bool> done, string what)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        done().ShouldBeTrue(what);
    }

    private static string CompletedOrder(SolutionHarness h, string nid, decimal quantity)
    {
        Ok(h.Run(new CreateWorkOrderFromProcess { NId = nid, ProcessNId = "Coat_LN1" }));
        Ok(h.Run(new ReleaseWorkOrder { NId = nid }));
        Ok(h.Run(new SetWorkOrderStatusForced { NId = nid, Status = WorkOrderStates.Complete }));
        return nid;
    }

    [Fact]
    public async Task Web_api_needs_the_key_runs_the_workflow_and_acks_the_sender()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        await using var rig = await Rig.StartAsync(h);
        var document = """{"orders":[]}""";

        (await rig.PostInAsync("SalesOrder", document, key: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await rig.PostInAsync("SalesOrder", document, key: "wrong")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await rig.PostInAsync("Nothing", document)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Workflow gọi tới command; command từ chối thì người gửi nhận 422 và Ack báo lỗi.
        var bad = await rig.PostInAsync("SalesOrder", "not json");
        bad.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await Until(() => rig.Systems.Of("ERP").Any(m => m.Path == "/erp/ack"), "ack");
        JsonNode.Parse(rig.Systems.Of("ERP").Single().Content)!["ok"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Sales_order_comes_in_through_the_web_api_round_trip()
    {
        var (h, _) = await Sh.StackAsync();
        await using var rig = await Rig.StartAsync(h);
        Sh.BoxOrder(h, "C000000099", 6);
        var order = Sh.OrderB("EXT-1", "C000000099", 6);
        var response = await rig.PostInAsync("SalesOrder", new JsonObject { ["orders"] = new JsonArray(order) }.ToJsonString());
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        h.Read<SalesOrder>().ShouldNotBeEmpty();
        await Until(() => rig.Systems.Of("ERP").Any(), "ack");
        JsonNode.Parse(rig.Systems.Of("ERP").Single().Content)!["ok"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Containment_from_the_quality_system_holds_the_lot_and_acks()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        await using var rig = await Rig.StartAsync(h);
        Ok(h.Run(new CreateMtu { NId = "CT-LOT", MaterialNId = "NV-RM-SEP", Quantity = 1 }));

        var response = await rig.PostInAsync("Containment", """{"Name":"From trace","SourceSystem":"QualityTrace","MtuNIds":["CT-LOT"]}""");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        h.Read<Mtu>(q => q.Where(m => m.NId == "CT-LOT")).Single().InContainment.ShouldBeTrue();
        await Until(() => rig.Systems.Of("QualityTrace").Any(), "ack");
    }

    [Fact]
    public async Task Direct_order_file_is_read_once_and_a_bad_file_moves_to_error()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        await using var rig = await Rig.StartAsync(h);
        var folder = Path.Combine(rig.Folder, "in");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "a.tmp"), """[{"poNo":"PF-1","poLine":"10","material":"NV-RM-SEP","batch":"FILEB","quantity":5,"expirationDate":"20270101"}]""", TestContext.Current.CancellationToken);
        File.Move(Path.Combine(folder, "a.tmp"), Path.Combine(folder, "a.json"));
        await File.WriteAllTextAsync(Path.Combine(folder, "b.json"), "garbage", TestContext.Current.CancellationToken);

        await Until(() => File.Exists(Path.Combine(folder, "done", "a.json")) || File.Exists(Path.Combine(folder, "error", "a.json")), "file a");
        await Until(() => File.Exists(Path.Combine(folder, "error", "b.json")), "file b");
        File.Exists(Path.Combine(folder, "done", "a.json")).ShouldBeTrue();
        h.Read<DirectDeliveryLine>().ShouldHaveSingleItem().Batch.ShouldBe("FILEB");
    }

    [Fact]
    public async Task Customer_system_dead_during_the_synchronous_send_leaves_mom_unchanged_and_the_retry_works()
    {
        var (h, fake) = await Sh.StackAsync();
        var (delivery, sheet) = Sh.StagedB(h, fake, "GW", "C000000020", "2026-10-27", "1300");
        await using var rig = await Rig.StartAsync(h);
        var shipper = SolutionHarness.Caller("ship.nv1", "shipping");

        rig.Systems.CustomerUp = false;
        var dead = h.Ship(new ConfirmTruckLoad { DeliveryNId = delivery, SealNo = "SEAL-G", Skids = [sheet] }, shipper);
        Fg.Error(dead).ShouldBe(NvErrors.NotConfirmed);
        Fg.Container(h, "GW-P1").Status.ShouldBe(PalletStates.Staged);
        h.Read<Delivery>(q => q.Where(d => d.NId == delivery)).Single().ConfirmationNo.ShouldBeNull();

        rig.Systems.CustomerUp = true;
        Ok(h.Ship(new ConfirmTruckLoad { DeliveryNId = delivery, SealNo = "SEAL-G", Skids = [sheet] }, shipper));
        h.Read<Delivery>(q => q.Where(d => d.NId == delivery)).Single().ConfirmationNo.ShouldStartWith("CF-");
        rig.Systems.Of("CustomerShipSys").Select(m => m.Path).ShouldBe(["/customer/skid-build", "/customer/ship-load"]);
    }

    [Fact]
    public async Task Kanban_refresh_reads_from_the_supplier_portal_and_a_dead_portal_changes_nothing()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        var rig = await Rig.StartAsync(h);
        await using (rig)
        {
            rig.Systems.KanbanList = """[{"part":"NV-RM-CASE-A","dock":"L9","supplier":"SUP-1","kanban":"KB-EXT","quantity":100,"effectiveFrom":"2026-01-01"}]""";
            Ok(h.Run(new RefreshKanbans(), app: MapApp.Name));
            h.Read<Kanban>(q => q.Where(k => k.DockCode == "L9")).Single().KanbanNo.ShouldBe("KB-EXT");
        }

        // Gateway đã tắt: lệnh báo lỗi, dữ liệu giữ nguyên.
        var before = h.Read<Kanban>().Count;
        Fg.Error(h.Run(new RefreshKanbans(), app: MapApp.Name)).ShouldBe(NvErrors.InvalidKanban);
        h.Read<Kanban>().Count.ShouldBe(before);
    }

    [Fact]
    public async Task Wms_picklist_goes_out_and_its_status_completes_the_leg()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        await using var rig = await Rig.StartAsync(h);
        var line = SolutionHarness.Caller("op.nv1", "line-operator");
        var mover = SolutionHarness.Caller("log.nv1", "logistics");
        Ok(h.Run(new CreateMtu { NId = "WM-1", MaterialNId = "NV-RM-CASE-B", Quantity = 300, EquipmentNId = "NV1-CMN-PC-T2-02" }));
        Ok(h.Run(new AttachMtuToBuffer { BufferNId = "PC-T2-02", MtuNId = "WM-1" }));
        var first = h.Run(new CallMaterial { RouteNId = "RT-CASE-B", MaterialNId = "NV-RM-CASE-B", Quantity = 300 }, line, PullApp.Name);
        Ok(first);
        var task = h.Read<TransportOperation>().Single().NId;
        Ok(h.Run(new RecordLot { TaskNId = task, MtuNId = "WM-1" }, mover, PullApp.Name));
        Ok(h.Run(new CompletePullTask { TaskNId = task }, mover, PullApp.Name));

        await Until(() => rig.Systems.Of("WMS").Any(), "picklist");
        var picklist = JsonNode.Parse(rig.Systems.Of("WMS").Single().Content)!;
        var request = picklist["workOrder"]!.GetValue<string>();

        Ok(h.Run(new CreateMtu { NId = "WM-2", MaterialNId = "NV-RM-CASE-B", Quantity = 300, EquipmentNId = "NV1-CMN-RCV-R02" }));
        Ok(h.Run(new AttachMtuToBuffer { BufferNId = "RCV-R02", MtuNId = "WM-2" }));

        // Báo lần lượt: bỏ qua trạng thái khác, Picked ghi lô, Delivered hoàn tất.
        (await rig.PostInAsync("PicklistStatus", JsonSerializer.Serialize(new { WorkOrder = request, Status = "Queued" }))).EnsureSuccessStatusCode();
        (await rig.PostInAsync("PicklistStatus", JsonSerializer.Serialize(new { WorkOrder = request, Status = "Delivered" }))).EnsureSuccessStatusCode();
        h.Read<TransportOperation>(q => q.Where(t => t.LogisticRequestNId == request)).Single().Status.ShouldBe(TransportStatus.Released);
        (await rig.PostInAsync("PicklistStatus", JsonSerializer.Serialize(new { WorkOrder = request, Status = "Picked", LotId = "WM-2" }))).EnsureSuccessStatusCode();
        (await rig.PostInAsync("PicklistStatus", JsonSerializer.Serialize(new { WorkOrder = request, Status = "Delivered" }))).EnsureSuccessStatusCode();
        h.Read<Mtu>(q => q.Where(m => m.NId == "WM-2")).Single().EquipmentNId.ShouldBe("NV1-CMN-PC-T2-02");
        h.Read<TransportOperation>(q => q.Where(t => t.LogisticRequestNId == request)).Single().Status.ShouldBe(TransportStatus.Complete);
        (await rig.PostInAsync("PicklistStatus", JsonSerializer.Serialize(new { WorkOrder = "NONE", Status = "Picked" }))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Lab_L21_message_on_a_blocked_day_waits_in_the_queue_and_leaves_when_the_block_ends()
    {
        // Ngày 2 của tháng: chặn đầu tháng.
        var h = await SolutionHarness.WithMasterDataAsync(new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero));
        await using var rig = await Rig.StartAsync(h);
        CompletedOrder(h, "WO-L21", 0);
        h.DeliverEvents();

        // Triệu chứng: ERP chưa nhận gì, không có lỗi; đường chẩn đoán: dòng hàng đợi và cảnh báo trong trace.
        rig.Systems.Of("ERP").ShouldBeEmpty();
        var waiting = h.Read<OutboundQueueItem>(q => q.Where(i => i.WorkOrderNId == "WO-L21")).ShouldHaveSingleItem();
        (waiting.MessageClass, waiting.WorkOrderNId, waiting.Sent).ShouldBe((OutClasses.Production, "WO-L21", false));
        h.Traces.Records.ShouldContain(t => t.Message.Contains("schedule exception", StringComparison.Ordinal));

        // Timer vẫn chạy trong ngày bị chặn nhưng không gửi.
        h.RunClockUntil(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        rig.Systems.Of("ERP").ShouldBeEmpty();
        h.Read<OutboundQueueItem>(q => q.Where(i => i.WorkOrderNId == "WO-L21")).Single().Sent.ShouldBeFalse();

        // Hết chặn (giờ miền Đông): timer đầu tiên gửi.
        h.RunClockUntil(new DateTimeOffset(2026, 11, 5, 6, 0, 0, TimeSpan.Zero));
        await Until(() => rig.Systems.Of("ERP").Any(m => m.Path == "/erp/production"), "production report");
        var report = JsonNode.Parse(rig.Systems.Of("ERP").Single(m => m.Path == "/erp/production").Content)!;
        (report["workOrder"]!.GetValue<string>(), report["recordType"]!.GetValue<string>()).ShouldBe(("WO-L21", "I"));
        h.Read<OutboundQueueItem>(q => q.Where(i => i.WorkOrderNId == "WO-L21")).Single().Sent.ShouldBeTrue();
    }

    [Fact]
    public async Task Round_trip_through_kafka_creates_the_schedule_and_acks_on_the_other_topic()
    {
        await using var redpanda = new RedpandaBuilder("docker.redpanda.com/redpandadata/redpanda:v24.2.18").Build();
        await redpanda.StartAsync(TestContext.Current.CancellationToken);
        var bootstrap = redpanda.GetBootstrapAddress().Replace("plaintext://", string.Empty, StringComparison.Ordinal).TrimEnd('/');
        var h = await SolutionHarness.WithMasterDataAsync();
        await using var rig = await Rig.StartAsync(h, bootstrap);
        var payload = JsonSerializer.Serialize(new
        {
            route = "RTKAFKA",
            trailerNo = "T-KF",
            plannedOn = new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero),
            skids = new[] { new { poNo = "PO-KAFKA", supplier = "SUP-1", dock = "L1", pallet = "F", skid = "001", material = "NV-RM-CASE-A", kanban = "KB-CASE-A", quantityPerBox = 6m, boxCount = 50m } },
        });

        using var producer = new Confluent.Kafka.ProducerBuilder<string, string>(new Confluent.Kafka.ProducerConfig { BootstrapServers = bootstrap }).Build();
        await producer.ProduceAsync("nv.supplier.schedules", new Confluent.Kafka.Message<string, string> { Key = "k1", Value = payload }, TestContext.Current.CancellationToken);

        await Until(() => h.Read<InboundSchedule>(q => q.Where(s => s.Route == "RTKAFKA")).Count == 1, "schedule from kafka");

        using var consumer = new Confluent.Kafka.ConsumerBuilder<string, string>(new Confluent.Kafka.ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = "test-ack",
            AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe("nv.supplier.ack");
        var ack = consumer.Consume(TimeSpan.FromSeconds(30));
        ack.ShouldNotBeNull();
        JsonNode.Parse(ack.Message.Value)!["ok"]!.GetValue<bool>().ShouldBeTrue();
    }
}
