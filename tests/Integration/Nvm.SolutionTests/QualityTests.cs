using Nvm.Foundation;
using Nvm.Product;
using Nvm.Project;
using Nvm.Solution;
using Nvm.Solution.Suites;

namespace Nvm.SolutionTests;

/// <summary>
/// DoD R6: bộ suite chấp nhận viết lại từ luật của suite chất lượng của team (giá trị đã đổi), và các lab L01, L12–L15.
/// Master data NV1, cấu hình hủy theo ca (<c>suites/quality</c>), view danh sách NC; ca ngày 26/10 đã mở WO.
/// </summary>
public sealed class QualityTests
{
    private static readonly CallerIdentity Lead = SolutionHarness.Caller("lead.nv1", "line-lead");
    private static readonly CallerIdentity Inspector = SolutionHarness.Caller("qa.nv1", "quality");

    internal static async Task<SolutionHarness> QualityStackAsync(bool views = true)
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        h.Deploy(Path.Combine(SolutionHarness.SuitesRoot, "quality")).Where(s => s.Status == SuiteStatus.Failed).ShouldBeEmpty();
        if (views)
        {
            ReadingViews.Install(h.ConnectionString, SolutionHarness.Registry);
        }

        h.RunClockUntil(new DateTimeOffset(2026, 10, 26, 7, 1, 0, TimeSpan.Zero));
        return h;
    }

    [Fact]
    public async Task Acceptance_suites_rewritten_from_the_team_quality_rules_pass()
    {
        var h = await QualityStackAsync();
        var folder = Path.Combine(SolutionHarness.RepoRoot(), "tests", "Integration", "Nvm.SolutionTests", "Acceptance", "Quality");
        var failures = new List<string>();
        var steps = 0;
        foreach (var path in Directory.GetFiles(folder, "*.suite.json").Order(StringComparer.Ordinal))
        {
            var result = SuiteRunner.Run(path, h.Target());
            steps += result.Log.Count;
            failures.AddRange(result.Log.Where(l => l.Contains("Failed", StringComparison.Ordinal)).Select(l => result.Suite + ": " + l));
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
        steps.ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task Ok_coil_split_takes_the_configured_scrap_from_the_coil_and_the_parent_keeps_the_rest()
    {
        var h = await QualityStackAsync();

        var nc = Line(h, Coil("SPLIT-EC1"));

        nc.NotHandled.ShouldBeEmpty();
        h.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-ELEC-A_SPLIT-EC1")).Single().Quantity.ShouldBe(118m);
        var child = h.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-ELEC-A_SPLIT-EC1.1")).Single();
        child.Quantity.ShouldBe(2m);
        child.Status.ShouldBe(MtuStatus.Scrapped);
    }

    [Fact]
    public async Task Coil_message_after_its_cells_sets_the_coil_length_minus_what_the_cells_used_and_still_splits()
    {
        var h = await QualityStackAsync();

        // Đường poll đọc theo từng mã bước: tin cell (ca010) có thể tới trước tin cuộn (el010), tạo cuộn như một lô tạm.
        Line(h, Cell("ORD-C1", "ORD-EC1"), Cell("ORD-C2", "ORD-EC1")).NotHandled.ShouldBeEmpty();
        var nc = Line(h, Coil("ORD-EC1"));

        nc.NotHandled.ShouldBeEmpty();
        h.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-ELEC-A_ORD-EC1")).Single().Quantity.ShouldBe(120m - 2.4m - 2m);
        h.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-ELEC-A_ORD-EC1.1")).Single().Quantity.ShouldBe(2m);

        // MTU có serial (cell) giữ lượng 1: tin formation tới trước tin lắp cell thì tin cell vẫn ghi được.
        Line(h, Unit("fo010", "ORD-C3", end: "09:00", materials: [("Cell A in use", "ORD-C3")])).NotHandled.ShouldBeEmpty();
        Line(h, Cell("ORD-C3", "ORD-EC1")).NotHandled.ShouldBeEmpty();
        h.Read<LineExecution>(q => q.Where(e => e.Serial == "ORD-C3")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task L12_split_unit_M_against_coil_unit_m_fails_with_a_reasonless_message_and_the_reason_only_in_trace()
    {
        var h = await QualityStackAsync();
        h.Run(Config("M")).StatusCode.ShouldBe(200);

        var nc = Line(h, Coil("L12-EC1"));

        // Người gọi chỉ thấy câu chung; mã ghép về LineDB là -4410.
        var failed = nc.NotHandled.ShouldHaveSingleItem();
        failed.Error.Code.ShouldBe(LineErrors.SplitFailed);
        failed.Error.Message.ShouldBe("Failed to split MTU NV-WIP-ELEC-A_L12-EC1 for non-conformance processing.");
        LineErrors.Composite(LineProcessType.NonConformance, failed.Error.Code).ShouldBe(-4410);
        h.Traces.Records.ShouldContain(r => r.Message.Contains("unit 'M' of the scrap configuration is not the MTU unit 'm'", StringComparison.Ordinal));
        h.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-ELEC-A_L12-EC1")).Single().Quantity.ShouldBe(120m);
        h.Read<NonConformanceItem>(q => q.Where(i => i.MtuNId!.StartsWith("NV-WIP-ELEC-A_L12-EC1"))).ShouldBeEmpty();

        // Đối chứng: sửa đơn vị trong cấu hình cho khớp từng chữ, tin sau tách được.
        h.Run(Config("m")).StatusCode.ShouldBe(200);
        Line(h, Coil("L12-EC2")).NotHandled.ShouldBeEmpty();
        h.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-ELEC-A_L12-EC2.1")).Single().Quantity.ShouldBe(2m);
    }

    [Fact]
    public async Task L13_suspect_hold_on_a_scrapped_mtu_is_a_400_whose_body_carries_the_reason()
    {
        var h = await QualityStackAsync();
        Line(h, Coil("L13-EC1", "KO", "11"));

        var result = h.Run(new SuspectHoldMtu { MtuNId = "NV-WIP-ELEC-A_L13-EC1", DefectNId = "D-LEAK" }, Inspector);

        result.StatusCode.ShouldBe(400);
        result.As<NvResponse>().Error!.ErrorMessage.ShouldContain("already scrapped");
    }

    [Fact]
    public async Task L14_flow_mode_suite_keyed_without_the_flow_column_is_green_yet_skips_the_trial_row()
    {
        var h = await QualityStackAsync();
        var fixtures = Path.Combine(SolutionHarness.Fixtures(), "L14");

        // Bước phủ đã có dòng mã 00 (tách hao hụt): khóa kiểm thiếu cột cờ chạy thử thấy "đã có" và bỏ qua.
        var buggy = SuiteRunner.Run(Path.Combine(fixtures, "L14_KeyWithoutFlowMode.suite.json"), h.Target());
        buggy.Passed.ShouldBeTrue();
        buggy.Log.ShouldContain(l => l.EndsWith("SetConfig] Skipped", StringComparison.Ordinal));
        h.Read<OperationScrapConfig>(q => q.Where(c => c.OperationCode == "el010" && c.IsFlowMode)).ShouldBeEmpty();

        // Hệ quả: cuộn chạy thử đi qua bước phủ, mang dấu, không bị hủy.
        Line(h, Coil("L14-EC1", mode: RunModes.Flow)).NotHandled.ShouldBeEmpty();
        h.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-ELEC-A_L14-EC1")).Single().Status.ShouldBe(MtuStatus.Available);

        // Đối chứng: khóa đủ cột thì dòng chạy thử được tạo, cuộn chạy thử sau đó bị hủy ngay ở bước phủ.
        SuiteRunner.Run(Path.Combine(fixtures, "L14_KeyWithFlowMode.suite.json"), h.Target()).Passed.ShouldBeTrue();
        h.Read<OperationScrapConfig>(q => q.Where(c => c.OperationCode == "el010" && c.IsFlowMode)).ShouldHaveSingleItem();
        Line(h, Coil("L14-EC2", mode: RunModes.Flow)).NotHandled.ShouldBeEmpty();
        h.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-ELEC-A_L14-EC2")).Single().Status.ShouldBe(MtuStatus.Scrapped);
    }

    [Fact]
    public async Task L15_nc_list_reads_a_view_that_update_database_does_not_create_and_the_view_step_brings_it()
    {
        var h = await QualityStackAsync(views: false);
        Line(h, Coil("L15-EC1", "KO", "11"));
        h.Read<NonConformance>().ShouldNotBeEmpty();

        // Trong tiến trình lỗi lộ ra ngay; qua HTTP thì response đã bắt đầu với 200 rồi bị cắt (lab L15 ở R2).
        Should.Throw<Microsoft.Data.SqlClient.SqlException>(() => ReadList(h)).Message.ShouldContain("NonConformanceListRow");

        ReadingViews.Install(h.ConnectionString, SolutionHarness.Registry);
        var rows = ReadList(h);
        rows.Success.ShouldBeTrue();
        rows.Result["value"]!.AsArray().Count.ShouldBe(1);
    }

    private static StepOutcome ReadList(SolutionHarness h) =>
        h.Target().ReadFunction(ProductionApp.Name, "NvProductionExt_NonConformanceList", new System.Text.Json.Nodes.JsonObject(), new Dictionary<string, string>());

    [Fact]
    public async Task L01_composite_reports_an_error_after_its_first_step_already_held_the_mtu()
    {
        var h = await QualityStackAsync();
        Line(h, Coil("L01-EC1"));
        h.Run(new CreateMtu { NId = "L01-PART", MaterialNId = "NV-RM-ANODE", Quantity = 5, EquipmentNId = "NV1-B1-L1-ELEC-COAT-OP10-M1" }).StatusCode.ShouldBe(200);
        h.Run(new SetMaterialSupplier { MaterialNId = "NV-RM-ANODE", SupplierNId = "SUP-111" }).StatusCode.ShouldBe(200);

        // Anode có hai nhà cung cấp: bước đặt nhà cung cấp lỗi, nhưng NC và MTU OnHold của bước đầu đã commit.
        var result = h.Run(new DeclareSupplierIssue { MtuNId = "L01-PART", DefectNId = "D-LEAK" }, Inspector);

        result.As<NvResponse>().Succeeded.ShouldBeFalse();
        result.As<NvResponse>().Error!.ErrorMessage.ShouldStartWith("There was an error updating the non-conformance supplier");
        h.Read<Mtu>(q => q.Where(m => m.NId == "L01-PART")).Single().Status.ShouldBe(MtuStatus.OnHold);
        var item = h.Read<NonConformanceItem>(q => q.Where(i => i.MtuNId == "L01-PART")).ShouldHaveSingleItem();
        h.Read<NonConformance>(q => q.Where(n => n.NId == item.NonConformanceNId)).Single().Status.ShouldBe(NonConformanceStatus.Open);

        // Đối chứng: đưa nhà cung cấp vào lời gọi thì cả hai bước xong.
        h.Run(new CreateMtu { NId = "L01-PART2", MaterialNId = "NV-RM-ANODE", Quantity = 5, EquipmentNId = "NV1-B1-L1-ELEC-COAT-OP10-M1" });
        h.Run(new DeclareSupplierIssue { MtuNId = "L01-PART2", DefectNId = "D-LEAK", SupplierNId = "SUP-111" }, Inspector)
            .As<NvResponse>().Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Disassembly_needs_an_nc_lists_the_genealogy_and_can_be_undone()
    {
        var h = await QualityStackAsync();
        Line(h, Coil("DIS-EC1"), Cell("DIS-C1", "DIS-EC1"));
        Line(h, Unit("fo010", "DIS-C1", materials: [("Cell A in use", "DIS-C1")], end: "09:00"));
        const string Formed = "NV-WIP-FCELL-A_DIS-C1";

        h.Run(new ListDisassemblyComponents { ProductMtuNId = Formed }, Inspector).As<DisassemblyResponse>().Error!.ErrorMessage
            .ShouldBe("The NC doesn't exist.");

        h.Run(new SuspectHoldMtu { MtuNId = Formed, DefectNId = "D-LEAK" }, Inspector).StatusCode.ShouldBe(200);
        var tree = h.Run(new ListDisassemblyComponents { ProductMtuNId = Formed }, Inspector).As<DisassemblyResponse>();
        tree.Components.Select(c => (c.MtuNId, c.Level)).ShouldContain(("NV-WIP-CELL-A_DIS-C1", 1));
        tree.Components.ShouldContain(c => c.MtuNId == "NV-WIP-ELEC-A_DIS-EC1" && c.Level == 2 && !c.Allowed);
        var caseMtu = tree.Components.Single(c => c.MaterialNId == "NV-RM-CASE-A");
        caseMtu.Allowed.ShouldBeTrue();

        h.Run(new DisassembleProduct
        {
            ProductMtuNId = Formed,
            Components = [new DisassemblyChoice { MtuNId = "NV-WIP-ELEC-A_DIS-EC1", Action = DisassemblyActions.Remove }],
        }, Inspector).As<DisassemblyResponse>().Error!.ErrorCode.ShouldBe(NvErrors.PartNotAllowed);

        var done = h.Run(new DisassembleProduct
        {
            ProductMtuNId = Formed,
            Components = [new DisassemblyChoice { MtuNId = caseMtu.MtuNId, Action = DisassemblyActions.Scrap }],
        }, Inspector).As<DisassemblyResponse>();
        done.Succeeded.ShouldBeTrue();
        h.Read<Mtu>(q => q.Where(m => m.NId == caseMtu.MtuNId)).Single().Status.ShouldBe(MtuStatus.Scrapped);

        h.Run(new UndoDisassembly { LogNId = done.Logs.Single() }, Inspector).StatusCode.ShouldBe(200);
        h.Read<Mtu>(q => q.Where(m => m.NId == caseMtu.MtuNId)).Single().Status.ShouldBe(caseMtu.Status);
        h.Run(new UndoDisassembly { LogNId = done.Logs.Single() }, Inspector).StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task Imported_containment_request_is_renamed_after_the_ncm_code_in_its_name()
    {
        var h = await QualityStackAsync();
        Line(h, Coil("CR-EC1"));

        h.Run(new ImportContainmentRequest { Name = "Supplier lot NCM_8801 hold", SourceSystem = "QTRACE", MtuNIds = ["NV-WIP-ELEC-A_CR-EC1"] }, Inspector);
        h.Run(new ImportContainmentRequest { Name = "Second wave NCM_8801", SourceSystem = "QTRACE" }, Inspector);
        h.Run(new ImportContainmentRequest { Name = "No code here", SourceSystem = "QTRACE" }, Inspector);
        h.DeliverEvents();

        h.Read<ContainmentRequest>().Select(c => c.Name).Order(StringComparer.Ordinal).ShouldBe(["NCM_8801-0", "NCM_8801-1", "No code here"]);
        h.Read<ContainmentIdentifier>().Count.ShouldBe(2);
        h.Traces.Records.ShouldContain(r => r.Message.Contains("No NCM/PCR found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shrink_rows_from_linedb_are_split_from_the_lot_and_scrapped()
    {
        var h = await QualityStackAsync();
        var shrink = h.Run(new HandleLineShrink
        {
            Shrinks = [new LineShrinkDetail { OperationCode = "el010", EquipmentCode = "el010_m1_1", MaterialCode = "Cathode powder lot in use", Lot = "SH-1", Quantity = 4.2m, StatusCode = "50", EventTime = At("08:30") }],
        }, Lead).As<LineQualityResponse>();

        shrink.NotHandled.ShouldBeEmpty();
        h.Read<Mtu>(q => q.Where(m => m.NId == "NV-RM-CATHODE_SH-1")).Single().Quantity.ShouldBe(0m);
        h.Read<Mtu>(q => q.Where(m => m.NId == "NV-RM-CATHODE_SH-1.1")).Single().Quantity.ShouldBe(4.2m);

        // Gửi lại cùng tin: không tách lần nữa.
        h.Run(new HandleLineShrink
        {
            Shrinks = [new LineShrinkDetail { OperationCode = "el010", EquipmentCode = "el010_m1_1", MaterialCode = "Cathode powder lot in use", Lot = "SH-1", Quantity = 4.2m, StatusCode = "50", EventTime = At("08:30") }],
        }, Lead).As<LineQualityResponse>().NotHandled.ShouldBeEmpty();
        h.Read<Mtu>(q => q.Where(m => m.NId.StartsWith("NV-RM-CATHODE_SH-1."))).Count.ShouldBe(1);

        // Tin sau còn trừ lô SH-1: trừ vào lô mẹ (nâng số lượng), không vào phần hao hụt đã hủy dù nó còn số lượng lớn hơn.
        Line(h, Unit("el010", "SH-C1", quantity: 120, materials: [("Cathode powder lot in use", "SH-1")]));
        h.Read<MaterialConsumption>(q => q.Where(c => c.ProductMtuNId!.EndsWith("_SH-C1") && c.ComponentMtuNId!.StartsWith("NV-RM-CATHODE_SH-1")))
            .Single().ComponentMtuNId.ShouldBe("NV-RM-CATHODE_SH-1");
    }

    internal static DateTimeOffset At(string hhmm) =>
        new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero).Add(TimeSpan.Parse(hhmm, System.Globalization.CultureInfo.InvariantCulture));

    internal static LineOperationDetail Unit(string op, string serial, string status = "OK", string? code = null, string end = "08:00",
        decimal? quantity = null, (string Item, string Lot)[]? materials = null, string? mode = null) => new()
        {
            OperationCode = op,
            EquipmentCode = op + "_m1_1",
            Serial = serial,
            Quantity = quantity,
            StatusId = status,
            StatusCode = code ?? (status == "OK" ? "00" : "99"),
            EndTime = At(end),
            RunMode = mode,
            Materials = [.. (materials ?? []).Select(m => new LineMaterialUse { MaterialCode = m.Item, Lot = m.Lot })],
        };

    internal static LineOperationDetail Coil(string serial, string status = "OK", string? code = null, string? mode = null) =>
        Unit("el010", serial, status, code, quantity: 120, mode: mode);

    internal static LineOperationDetail Cell(string serial, string coil, string status = "OK", string? code = null) =>
        Unit("ca010", serial, status, code, "08:10", materials: [("Electrode coil A in use", coil)]);

    /// <summary>Như handler của team: ghi kết quả rồi xử lý NC cho các serial đã ghi.</summary>
    internal static LineQualityResponse Line(SolutionHarness h, params LineOperationDetail[] units)
    {
        var applied = h.Run(new ApplyLineResults { Operations = units }, Lead).As<ApplyLineResultsResponse>();
        applied.NotExecutedOperations.ShouldBeEmpty();
        return h.Run(new HandleLineNonConformances { Items = [.. applied.NonConformanceData] }, Lead).As<LineQualityResponse>();
    }

    private static SetOperationScrapConfig Config(string unit) => new()
    {
        NId = "OSC-L1-S1-el010-00",
        OperationCode = "el010",
        StatusCode = "00",
        ScheduleNId = "PS_NV1-B1-L1-ELEC-S1",
        AutoDispositionStatus = NonConformanceStatus.Scrap,
        SplitQuantity = 2,
        SplitUnitOfMeasure = unit,
        ScrapCode = "S1",
        ProblemCode = "P01",
        ModelCode = "STD",
        CreatingCostCenter = "CC-L1",
        ChargeToCostCenter = "CC-L1",
    };
}
