using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nvm.CommandStore;
using Nvm.Contracts.Ports;
using Nvm.ErpGateway;
using Nvm.Kernel.Commands;
using Nvm.MasterData.Commands;
using Nvm.MasterData.Hosting;
using Nvm.Material.Commands;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Hosting;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;

namespace Nvm.IntegrationTests;

/// <summary>
/// M11: B2MML bẩn từ ERP. Bốn mã về một vật liệu; file hỏng vào rejected kèm lý do mà watcher vẫn chạy; mã lạ và đơn
/// vị lệch thành task, lệnh không mất; cùng lịch gửi hai lần chỉ tạo một work order; backflush không mất, không chồng lô.
/// </summary>
public sealed class ErpGatewayTests(SqlCommandStoreFixture sql) : IClassFixture<SqlCommandStoreFixture>, IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nvm-erp-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task DirtyErpFiles_EndAsReleasedOrPendingWithTasks_NeverCrashNeverSilent()
    {
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        var clock = new FakeTimeProvider(T0);
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock);
        var options = Options("NV1");
        await Ok(host, new DefineCanonicalItemCommand("NV1", "pm", "item-cam", T0, IdentityKinds.Material, "MAT-0009812",
            "NMC811 cathode active material", "kg"));
        await Ok(host, new DefineCanonicalItemCommand("NV1", "pm", "item-product", T0, IdentityKinds.Product, "NV-P120-NMC",
            "Pack 120 kWh NMC", null));
        foreach (var alias in new[] { "9812", "MAT9812", "nmc811-cam" })
        {
            await Ok(host, new MapIdentityAliasCommand("NV1", "pm", "alias-" + alias, T0, IdentityKinds.Material, alias,
                "MAT-0009812", "Mã ERP cũ của cùng vật liệu"));
        }

        Directory.CreateDirectory(options.Inbound);
        Write(options, "001-ps-0834.xml", Schedule("PS-0834", "WO-0042", "NV-P120-NMC ", "MAT-0009812", "480", "kg"));
        Write(options, "002-ps-0835.xml", Schedule("PS-0835", "WO-0043", "NV-P120-NMC", "9812", "480", "kg"));
        Write(options, "003-ps-0836.xml", Schedule("PS-0836", "WO-0044", "NV-P120-NMC", "MAT9812", "480", "kg"));
        Write(options, "004-ps-0837.xml", Schedule("PS-0837", "WO-0045", "NV-P120-NMC", "NMC811-CAM", "480", "kg"));
        Write(options, "005-bad-quantity.xml", Schedule("PS-0900", "WO-0900", "NV-P120-NMC", "MAT-0009812", "abc", "kg"));
        Write(options, "006-ps-0838.xml", Schedule("PS-0838", "WO-0046", "NV-P120-NMC", "MAT-0009999", "12", "kg"));
        Write(options, "007-ps-0834-again.xml", Schedule("PS-0834", "WO-0042", "NV-P120-NMC ", "MAT-0009812", "480", "kg"));
        Write(options, "008-ps-0839.xml", Schedule("PS-0839", "WO-0047", "NV-P120-NMC", "MAT-0009812", "480000", "g"));
        Write(options, "009-not-xml.xml", "<<< this is not b2mml");

        var inbound = new ErpInboundProcessor(host.GetRequiredService<IServiceScopeFactory>(), options, clock,
            NullLogger<ErpInboundProcessor>.Instance);
        var run = await inbound.RunOnceAsync(Ct);
        run.ShouldBe(new InboundRunSummary(9, 7, 2, 0, 7));
        Directory.EnumerateFiles(options.Inbound).ShouldBeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(options.Rejected, "005-bad-quantity.xml.error.txt"), Ct))
            .ShouldContain("QuantityString");
        (await File.ReadAllTextAsync(Path.Combine(options.Rejected, "009-not-xml.xml.error.txt"), Ct))
            .ShouldContain("XML không đọc được");

        var orders = (await host.GetRequiredService<WorkOrderQueries>().ListAsync("NV1", null, 100, Ct))
            .ToDictionary(o => o.WorkOrderId);
        orders.Count.ShouldBe(6);   // lịch PS-0834 gửi hai lần vẫn là một work order
        foreach (var id in new[] { "WO-0042", "WO-0043", "WO-0044", "WO-0045" })
        {
            orders[id].Status.ShouldBe(WorkOrderStatuses.Released);
            orders[id].ProductCode.ShouldBe("NV-P120-NMC");
            orders[id].Materials.Single().MaterialId.ShouldBe("MAT-0009812");
        }
        orders["WO-0046"].Status.ShouldBe(WorkOrderStatuses.PendingMasterData);
        orders["WO-0047"].Status.ShouldBe(WorkOrderStatuses.PendingMasterData);
        orders["WO-0047"].Materials.Single().ShouldSatisfyAllConditions(
            m => m.Quantity.ShouldBe(480000m), m => m.UnitOfMeasure.ShouldBe("g"));   // không quy đổi âm thầm

        var tasks = await host.GetRequiredService<MasterDataQueries>().TasksAsync("NV1", "Open", Ct);
        tasks.Count.ShouldBe(2);
        tasks.ShouldContain(t => t.IssueKind == ReconciliationIssueKinds.UnknownMaterial && t.ExternalCode == "MAT-0009999");
        var uom = tasks.Single(t => t.IssueKind == ReconciliationIssueKinds.UomMismatch);
        uom.Detail.ShouldBe("ERP gửi 480000 g cho MAT-0009812, đơn vị gốc là kg.");

        var reevaluate = new PendingWorkOrderReevaluator(host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<MasterDataQueries>(), host.GetRequiredService<WorkOrderQueries>(), options, clock);
        (await reevaluate.RunOnceAsync(Ct)).ShouldBe(0);
        (await reevaluate.RunOnceAsync(Ct)).ShouldBe(0);   // cùng revision: phát lại kết quả, không làm lại

        await Ok(host, new DefineCanonicalItemCommand("NV1", "pm", "item-9999", T0, IdentityKinds.Material, "MAT-0009999",
            "Separator film", "kg"));
        (await reevaluate.RunOnceAsync(Ct)).ShouldBe(1);
        var unknown = tasks.Single(t => t.IssueKind == ReconciliationIssueKinds.UnknownMaterial);
        (await host.DispatchAsync(new AcceptReconciliationTaskCommand("NV1", "pm", "accept-unknown", T0, unknown.TaskId, "thử"),
            Ct)).ReasonCode.ShouldBe(MasterDataReasonCodes.TaskNotOpen);   // khai báo mã chuẩn đã đóng task mã lạ
        await Ok(host, new AcceptReconciliationTaskCommand("NV1", "pm", "accept-uom-2", T0, uom.TaskId,
            "ERP gửi gram cho lô thử nghiệm; kho xác nhận 480 kg"));
        (await reevaluate.RunOnceAsync(Ct)).ShouldBe(1);
        var after = (await host.GetRequiredService<WorkOrderQueries>().ListAsync("NV1", WorkOrderStatuses.PendingMasterData, 10, Ct));
        after.ShouldBeEmpty();
        (await host.GetRequiredService<MasterDataQueries>().TasksAsync("NV1", "Open", Ct)).ShouldBeEmpty();

        // Cùng ID lịch nhưng nội dung khác: không ghi đè lệnh cũ, file vào rejected với lý do.
        Write(options, "010-ps-0834-edited.xml", Schedule("PS-0834", "WO-0042", "NV-P120-NMC", "MAT-0009812", "500", "kg"));
        (await inbound.RunOnceAsync(Ct)).ShouldBe(new InboundRunSummary(1, 0, 1, 0, 0));
        (await File.ReadAllTextAsync(Path.Combine(options.Rejected, "010-ps-0834-edited.xml.error.txt"), Ct))
            .ShouldContain("ID mới");
    }

    [Fact]
    public async Task Backflush_ResendsTheSameBatchUntilErpAccepts_ThenAdvances()
    {
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        var clock = new FakeTimeProvider(T0);
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock);
        const string cell = "NV1CL16237A51001";
        (await host.DispatchAsync(new SerializeUnitCommand("NV1", "op", "birth-" + cell, cell, T0, TraceabilityFixtureSeed.ProductCode,
            "WO-0042", TraceabilityFixtureSeed.RoutingVersion), Ct)).Accepted.ShouldBeTrue();
        foreach (var (from, to) in new[] { (100m, 100.82m), (100.82m, 101.64m) })
        {
            (await host.DispatchAsync(new ConsumeMaterialCommand("NV1", "op", $"use-{from}", T0, cell, "ROLL-BF", "Roll", "ANODE",
                to - from, "m", from, to, "RUN-BF"), Ct)).Accepted.ShouldBeTrue();
        }

        var received = new List<(string Key, JsonDocument Body)>();
        var failFirst = true;
        await using var erp = await StartErpAsync(async context =>
        {
            received.Add((context.Request.Headers["Idempotency-Key"].ToString(),
                await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: Ct)));
            context.Response.StatusCode = failFirst ? 500 : 200;
            failFirst = false;
        });
        var options = Options("NV1");
        options.ErpBaseAddress = new Uri(erp.Urls.Single() + "/");
        using var http = new HttpClient();
        var publisher = new BackflushPublisher(host.GetRequiredService<SqlCommandStoreOptions>(), options, http, clock,
            NullLogger<BackflushPublisher>.Instance);

        var first = await publisher.RunOnceAsync(Ct);
        first.Sent.ShouldBeFalse();
        first.ErpStatus.ShouldBe(500);
        var second = await publisher.RunOnceAsync(Ct);
        second.Sent.ShouldBeTrue();
        second.BatchId.ShouldBe(first.BatchId);
        received.Count.ShouldBe(2);
        received[0].Key.ShouldBe(received[1].Key);
        var line = received[1].Body.RootElement.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("lotId").GetString().ShouldBe("ROLL-BF");
        line.GetProperty("quantity").GetDecimal().ShouldBe(1.64m);
        line.GetProperty("consumptions").GetInt32().ShouldBe(2);
        (await publisher.RunOnceAsync(Ct)).ShouldBe(new BackflushRunResult(false, null, 0, null));
        received.Count.ShouldBe(2);
        foreach (var (_, body) in received)
        { body.Dispose(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        { Directory.Delete(_root, recursive: true); }
    }

    private ErpGatewayOptions Options(string site) => new()
    {
        Enabled = true,
        SiteId = site,
        RootDirectory = Path.Combine(_root, site),
        ActorId = "system:erp-gateway"
    };

    private static void Write(ErpGatewayOptions options, string name, string content)
    {
        Directory.CreateDirectory(options.Inbound);
        File.WriteAllText(Path.Combine(options.Inbound, name), content);
    }

    internal static string Schedule(string scheduleId, string workOrderId, string product, string material, string quantity,
        string uom) => string.Create(CultureInfo.InvariantCulture, $"""
        <?xml version="1.0" encoding="utf-8"?>
        <ProductionSchedule xmlns="http://www.mesa.org/xml/B2MML-V0600">
          <ID>{scheduleId}</ID>
          <ProductionRequest>
            <ID>{workOrderId}</ID>
            <ProductProductionRule><ID>{product}</ID></ProductProductionRule>
            <EarliestStartTime>2026-08-25T06:00:00+07:00</EarliestStartTime>
            <SegmentRequirement>
              <MaterialRequirement>
                <MaterialDefinitionID>{material}</MaterialDefinitionID>
                <Quantity><QuantityString>{quantity}</QuantityString><UnitOfMeasure>{uom}</UnitOfMeasure></Quantity>
              </MaterialRequirement>
            </SegmentRequirement>
          </ProductionRequest>
        </ProductionSchedule>
        """);

    private static async Task Ok(ServiceProvider host, DurableCommand command)
    {
        var result = await host.DispatchAsync<DomainCommandResult>(command, Ct);
        result.Accepted.ShouldBeTrue($"{command.CommandType}: {result.ReasonCode} {result.ReasonText}");
    }

    private static async Task<WebApplication> StartErpAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapPost("/api/backflush", handler);
        await app.StartAsync(Ct);
        return app;
    }
}
