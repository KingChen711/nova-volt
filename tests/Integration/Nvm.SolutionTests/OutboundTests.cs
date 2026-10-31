using System.Text.Json.Nodes;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Product;
using Nvm.Project;

namespace Nvm.SolutionTests;

/// <summary>R11: lịch chặn theo giờ miền Đông, báo sản lượng và tiêu hao, file scrap ngày và tháng, đặt bù theo genealogy.</summary>
public sealed class OutboundTests
{
    private static readonly CallerIdentity Qa = SolutionHarness.Caller("qa.nv1", "quality");

    private static void Ok(CommandResult result) => result.StatusCode.ShouldBe(200, Fg.Message(result));

    private static async Task<(SolutionHarness, FakeGateway)> StackAsync(DateTimeOffset? start = null)
    {
        var h = await SolutionHarness.WithMasterDataAsync(start);
        var gateway = new FakeGateway();
        h.Executor.Gateway = gateway;
        return (h, gateway);
    }

    private static string Status(CommandResult result)
    {
        Ok(result);
        return result.Body["Status"]!.GetValue<string>();
    }

    private static CommandResult Out(SolutionHarness h, object command) => h.Run(command, app: OutApp.Name);

    private static void Order(SolutionHarness h, string nid)
    {
        Ok(h.Run(new CreateWorkOrderFromProcess { NId = nid, ProcessNId = "Coat_LN1" }));
        Ok(h.Run(new ReleaseWorkOrder { NId = nid }));
    }

    private static void Complete(SolutionHarness h, string nid)
    {
        Ok(h.Run(new SetWorkOrderStatusForced { NId = nid, Status = WorkOrderStates.Complete }));
        h.DeliverEvents();
    }

    /// <summary>Hủy một lô bằng màn kết luận NC, cấp scrap tag như người dùng làm.</summary>
    private static void Scrap(SolutionHarness h, string mtu, string scrapCode = "S2")
    {
        var created = h.Run(new CreateBulkNonConformance { AreaEquipmentNId = "NV1-B1-L1-MOD-ASSY", MtuNIds = [mtu] }, Qa);
        Ok(created);
        var nc = created.Body["NId"]!.GetValue<string>();
        Ok(h.Run(new SentenceNonConformances
        {
            NIds = [nc],
            Status = NonConformanceStatus.Scrap,
            CreatingCostCenter = "CC-L1",
            ChargeToCostCenter = "CC-L2",
            ScrapCode = scrapCode,
            ProblemCode = "P02",
            ModelCode = "STD",
        }, Qa));
    }

    private static void Lot(SolutionHarness h, string nid, string material, decimal quantity = 5) =>
        Ok(h.Run(new CreateMtu { NId = nid, MaterialNId = material, Quantity = quantity, EquipmentNId = "NV1-B1-L1-MOD-ASSY" }));

    [Theory]
    [InlineData("2026-11-01T14:00:00Z", "Queued")] // ngày 1
    [InlineData("2026-11-04T14:00:00Z", "Queued")] // ngày 4 còn chặn
    [InlineData("2026-11-05T14:00:00Z", "Empty")] // ngày 5 hết chặn
    [InlineData("2026-11-15T14:00:00Z", "Queued")] // Chủ nhật thứ 3
    [InlineData("2026-11-08T14:00:00Z", "Empty")] // Chủ nhật thứ 2
    [InlineData("2026-11-16T03:00:00Z", "Queued")] // UTC đã qua thứ Hai, miền Đông còn Chủ nhật
    [InlineData("2026-11-16T06:00:00Z", "Empty")] // miền Đông đã sang thứ Hai
    [InlineData("2026-12-01T03:00:00Z", "Empty")] // UTC đã ngày 1, miền Đông còn 30/11
    [InlineData("2026-12-01T06:00:00Z", "Queued")] // miền Đông đã ngày 1
    public async Task Block_follows_the_eastern_calendar(string now, string expected)
    {
        var (h, _) = await StackAsync(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        Status(Out(h, new SendConsumptionReport { WorkOrderNId = "WO-X" })).ShouldBe(expected);
    }

    [Fact]
    public async Task Class_without_a_block_row_always_sends_and_last_weekday_of_the_month_blocks()
    {
        var (h, _) = await StackAsync(new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero));
        Ok(Out(h, new SetMessageBlock { MessageClass = OutClasses.Consumption, WeekInstance = 9, DayOfWeek = "Sunday" }));
        Status(Out(h, new SendConsumptionReport { WorkOrderNId = "WO-X" })).ShouldBe(OutStatus.Empty);

        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 29, 14, 0, 0, TimeSpan.Zero));
        Status(Out(h, new SendConsumptionReport { WorkOrderNId = "WO-X" })).ShouldBe(OutStatus.Queued);
    }

    [Fact]
    public async Task Work_order_complete_sends_production_then_consumption_without_scrapped_products()
    {
        var (h, gateway) = await StackAsync();
        Order(h, "WO-R");
        Lot(h, "R-P1", "NV-WIP-ELEC-A", 1);
        Lot(h, "R-P2", "NV-WIP-ELEC-A", 1);
        Lot(h, "R-C", "NV-RM-ANODE", 100);
        Ok(h.Run(new RecordConsumption { WorkOrderNId = "WO-R", ProductMtuNId = "R-P1", ComponentMtuNId = "R-C", Quantity = 4 }));
        Ok(h.Run(new RecordConsumption { WorkOrderNId = "WO-R", ProductMtuNId = "R-P1", ComponentMtuNId = "R-C", Quantity = 3 }));
        Ok(h.Run(new RecordConsumption { WorkOrderNId = "WO-R", ProductMtuNId = "R-P2", ComponentMtuNId = "R-C", Quantity = 9 }));
        Scrap(h, "R-P2");
        gateway.Posted.Clear();
        Complete(h, "WO-R");

        gateway.Posted.Select(m => m.Adapter).ShouldBe([OutAdapters.ErpProduction, OutAdapters.ErpConsumption]);
        var consumption = JsonNode.Parse(gateway.Posted[1].Content)!;
        var line = consumption["lines"]!.AsArray().ShouldHaveSingleItem()!;
        (line["material"]!.GetValue<string>(), line["quantity"]!.GetValue<decimal>()).ShouldBe(("NV-RM-ANODE", 7m));

        // Cùng số lượng: không báo lại. Không có WO: lỗi.
        Status(Out(h, new SendProductionReport { WorkOrderNId = "WO-R" })).ShouldBe(OutStatus.Skipped);
        Fg.Error(Out(h, new SendProductionReport { WorkOrderNId = "NONE" })).ShouldBe(ProductErrors.UnknownWorkOrder);
    }

    [Fact]
    public async Task Status_changes_other_than_complete_send_nothing()
    {
        var (h, gateway) = await StackAsync();
        Order(h, "WO-N");
        h.DeliverEvents();
        gateway.Posted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Queue_sends_at_most_twenty_per_run_oldest_first_and_keeps_the_rest()
    {
        var (h, gateway) = await StackAsync(new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero));
        for (var i = 0; i < 22; i++)
        {
            Status(Out(h, new SendConsumptionReport { WorkOrderNId = $"WO-Q{i:00}" })).ShouldBe(OutStatus.Queued);
        }

        // Tin chờ cùng khóa không nhân đôi.
        Status(Out(h, new SendConsumptionReport { WorkOrderNId = "WO-Q00" })).ShouldBe(OutStatus.Queued);
        h.Read<OutboundQueueItem>().Count.ShouldBe(22);

        Out(h, new SendQueuedMessages()).Body["Count"]!.GetValue<int>().ShouldBe(0);
        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.Zero));
        Out(h, new SendQueuedMessages()).Body["Count"]!.GetValue<int>().ShouldBe(20);
        h.Read<OutboundQueueItem>().Count(q => !q.Sent).ShouldBe(2);
        Out(h, new SendQueuedMessages()).Body["Count"]!.GetValue<int>().ShouldBe(2);
        gateway.Posted.ShouldBeEmpty();
    }

    [Fact]
    public async Task Daily_scrap_file_leaves_out_lines_without_tag_or_supplier_and_sends_each_line_once()
    {
        var (h, gateway) = await StackAsync();
        Lot(h, "SP-1", "NV-RM-ANODE");
        Ok(h.Run(new CreateMaterial { NId = "NV-NOSUP", Name = "No supplier", UnitOfMeasure = "pc" }));
        Lot(h, "SP-NOSUP", "NV-NOSUP", 1);
        Scrap(h, "SP-1");
        Scrap(h, "SP-NOSUP");

        Out(h, new SendScrapFile()).Body["Count"]!.GetValue<int>().ShouldBe(1);
        var file = gateway.Posted.ShouldHaveSingleItem();
        (file.Adapter, file.MessageType).ShouldBe((OutAdapters.ScrapFile, "ScrapDaily"));
        file.Content.ShouldContain("NV-RM-ANODE,PANV-RM-ANODE,SUP-110,CC-L2,5,S2,STD,SC");
        file.Content.ShouldNotContain("SP-NOSUP");
        var error = h.Read<ScrapMessageLine>(q => q.Where(l => l.Status == OutStatus.Error)).Single();
        error.Reason!.ShouldContain("No supplier");

        Status(Out(h, new SendScrapFile())).ShouldBe(OutStatus.Empty);
        gateway.Posted.Count.ShouldBe(1);

        // Dữ liệu sửa xong: lần sau dòng ERROR đi.
        Ok(h.Run(new SetMaterialSupplier { MaterialNId = "NV-NOSUP", SupplierNId = "SUP-1" }));
        Out(h, new SendScrapFile()).Body["Count"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Monthly_scrap_runs_on_its_day_only_and_takes_supplier_codes()
    {
        var (h, gateway) = await StackAsync(new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero));
        Lot(h, "M-S2", "NV-RM-ANODE");
        Lot(h, "M-S3", "NV-RM-CATHODE");
        Scrap(h, "M-S2", "S2");
        Scrap(h, "M-S3", "S3");

        Status(Out(h, new SendScrapFile { Monthly = true, MonthlyTriggerDay = 10 })).ShouldBe(OutStatus.Skipped);
        h.Time.SetUtcNow(new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero));
        Out(h, new SendScrapFile { Monthly = true, MonthlyTriggerDay = 10 }).Body["Count"]!.GetValue<int>().ShouldBe(1);
        var file = gateway.Posted.ShouldHaveSingleItem();
        file.MessageType.ShouldBe("ScrapMonthly");
        file.Content.ShouldContain("NV-RM-ANODE");
        file.Content.ShouldNotContain("NV-RM-CATHODE");
    }

    [Fact]
    public void Parent_code_keeps_in_house_parts_and_changes_others() =>
        Enumerable.SequenceEqual(
            new[]
            {
                OutboundCodes.Parent("NV-WIP-CELL", OutMaterialClasses.InHouse),
                OutboundCodes.Parent("ABCDEFGHIJKL", "Component"),
                OutboundCodes.Parent("SHORT", null),
            },
            new[] { "NV-WIP-CELL", "PAABCDEFGHIJ", "PASHORT" }).ShouldBeTrue();

    [Fact]
    public async Task Reorder_follows_the_genealogy_and_takes_only_the_domestic_class_once_per_scrap()
    {
        var (h, gateway) = await StackAsync();
        Order(h, "WO-G");
        Lot(h, "G-CASE", "NV-RM-CASE-A", 50);
        Lot(h, "G-ANODE", "NV-RM-ANODE", 50);
        Lot(h, "G-MID", "NV-WIP-ELEC-A", 1);
        Lot(h, "G-TOP", "NV-FG-MOD-B60", 1);
        Ok(h.Run(new RecordConsumption { WorkOrderNId = "WO-G", ProductMtuNId = "G-MID", ComponentMtuNId = "G-CASE", Quantity = 6 }));
        Ok(h.Run(new RecordConsumption { WorkOrderNId = "WO-G", ProductMtuNId = "G-MID", ComponentMtuNId = "G-ANODE", Quantity = 2 }));
        Ok(h.Run(new RecordConsumption { WorkOrderNId = "WO-G", ProductMtuNId = "G-TOP", ComponentMtuNId = "G-MID", Quantity = 1 }));
        Scrap(h, "G-TOP");

        Out(h, new SendScrapReorder()).Body["Count"]!.GetValue<int>().ShouldBe(1);
        var message = gateway.Posted.ShouldHaveSingleItem();
        message.Adapter.ShouldBe(OutAdapters.Reorder);
        message.Content.Trim().ShouldEndWith(",L1,NV-RM-CASE-A,6");
        message.Content.ShouldNotContain("NV-RM-ANODE");

        h.Time.Advance(TimeSpan.FromMinutes(1));
        Status(Out(h, new SendScrapReorder())).ShouldBe(OutStatus.Empty);
        gateway.Posted.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Timers_run_the_queue_daily_and_monthly_jobs_from_master_data()
    {
        var (h, gateway) = await StackAsync(new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero));
        Lot(h, "TM-1", "NV-RM-ANODE");
        Scrap(h, "TM-1");

        // Ngày bị chặn: file scrap vào hàng đợi; hết chặn thì đi bằng timer.
        h.RunClockUntil(new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.Zero));
        gateway.Posted.ShouldBeEmpty();
        h.Read<OutboundQueueItem>().ShouldContain(q => q.MessageClass == OutClasses.Scrap && !q.Sent);
        h.RunClockUntil(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));

        // Ngày 5 cũng là ngày chạy file tháng, nên có cả hai file; file ngày chỉ một lần dù nhiều ngày chờ.
        gateway.Posted.Where(m => m.Adapter == OutAdapters.ScrapFile).Select(m => m.MessageType).ShouldBe(["ScrapDaily", "ScrapMonthly"], ignoreOrder: true);
    }
}
