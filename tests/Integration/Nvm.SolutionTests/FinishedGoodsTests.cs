using Microsoft.Data.SqlClient;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Product;
using Nvm.Project;
using Nvm.Solution;

namespace Nvm.SolutionTests;

/// <summary>
/// R7: cất kho theo địa chỉ part, bản ghi FIFO, bước đo cuối, dựng lại pallet, kiểm pallet bằng procedure, gỡ pallet, gói lẻ, và
/// lab L03, L17, L18. Pallet dựng bằng command (không qua LineDB); đường tin máy tới pallet ở <see cref="PackingLineTests"/>.
/// </summary>
public sealed class FinishedGoodsTests
{
    private static readonly CallerIdentity Shipper = SolutionHarness.Caller("ship.nv1", "shipping");
    private static readonly CallerIdentity Inspector = SolutionHarness.Caller("qa.nv1", "quality");

    [Fact]
    public async Task Storing_needs_a_full_pallet_and_a_store_address_of_its_part_then_a_fifo_record_follows_the_commit()
    {
        var h = await Fg.StackAsync();
        Fg.Pallet(h, "ST-P1", "NV-FG-MOD-B60", totes: 6);
        Fg.Pallet(h, "ST-P2", "NV-FG-MOD-B60", totes: 6);
        h.Run(new SetMtuaStatusForced { NId = "ST-P2", Status = PalletStates.Empty }).StatusCode.ShouldBe(200);

        Fg.Error(h.Ship(new StorePallet { PalletNId = "ST-P1", LocationNId = "NV1-CMN-FGW-A01" }, Shipper)).ShouldBe(NvErrors.NotAStoreAddress);
        Fg.Error(h.Ship(new StorePallet { PalletNId = "ST-P2", LocationNId = "NV1-CMN-FGW-B01" }, Shipper)).ShouldBe(NvErrors.InvalidPallet);
        Fg.Pallet(h, "ST-P3", "NV-FG-MOD-A", totes: 8);
        Fg.Error(h.Ship(new StorePallet { PalletNId = "ST-P3", LocationNId = "NV1-CMN-FGW-A01" }, Shipper)).ShouldBe(NvErrors.NoStoreAddress);

        h.Ship(new StorePallet { PalletNId = "ST-P1", LocationNId = "NV1-CMN-FGW-B01" }, Shipper).StatusCode.ShouldBe(200);

        // Post-action chỉ hẹn event; bản ghi FIFO có khi subscriber chạy, sau response.
        h.Read<FifoPallet>().ShouldBeEmpty();
        h.DeliverEvents();
        var record = h.Read<FifoPallet>().ShouldHaveSingleItem();
        (record.NId, record.LocationNId, record.Status, record.MaterialNId).ShouldBe(("ST-P1", "NV1-CMN-FGW-B01", PalletStates.Full, "NV-FG-MOD-B60"));
        record.BuildDate.ShouldNotBeNull();
        Fg.Container(h, "ST-P1").EquipmentNId.ShouldBe("NV1-CMN-FGW-B01");
    }

    [Fact]
    public async Task Part_needing_the_end_of_line_test_blocks_storing_until_kits_are_tested_and_the_pallet_rebuilt()
    {
        var h = await Fg.StackAsync();
        Fg.Pallet(h, "EV-P1", "NV-FG-KIT-A", totes: 8);

        var blocked = h.Ship(new StorePallet { PalletNId = "EV-P1", LocationNId = "NV1-CMN-FGW-A01" }, Shipper);
        Fg.Error(blocked).ShouldBe(NvErrors.NotTested);
        Fg.Message(blocked).ShouldContain("no passing end-of-line test");
        Fg.Container(h, "EV-P1").EquipmentNId.ShouldBe(EquipmentLevels.NotApplicable);

        // Tin đo cuối của từng kit: kit giữ MTU, tote chứa nó rời pallet; pallet hết tote thì Empty, giữ vị trí.
        Fg.Test(h, [.. Enumerable.Range(1, 8).Select(i => $"EV-P1-K{i:00}")]).NotExecutedOperations.ShouldBeEmpty();
        var old = Fg.Container(h, "EV-P1");
        (old.Status, old.Quantity, old.MaterialNId).ShouldBe((PalletStates.Empty, 0m, null));
        h.Read<Mtua>(q => q.Where(c => c.ParentMtuaNId == "EV-P1")).ShouldBeEmpty();

        // Dựng lại: tote của kit chưa đo bị từ chối; đủ 8 tote thì Completed → Full.
        Fg.Pallet(h, "EV-X", "NV-FG-KIT-A", totes: 1);
        h.Run(new StartPalletRebuild { PalletNId = "EV-R1" }, app: ProductionApp.Name).StatusCode.ShouldBe(200);
        Fg.Error(h.Run(new AddToteToRebuiltPallet { PalletNId = "EV-R1", ToteNId = "EV-X-T01" })).ShouldBe(NvErrors.NotTested);
        for (var i = 1; i <= 8; i++)
        {
            if (i == 8)
            {
                Fg.Error(h.Run(new CompletePalletRebuild { PalletNId = "EV-R1" })).ShouldBe(NvErrors.PalletNotComplete);
            }

            var added = h.Run(new AddToteToRebuiltPallet { PalletNId = "EV-R1", ToteNId = $"EV-P1-T{i:00}" });
            added.StatusCode.ShouldBe(200);
            (added.As<RebuildResponse>().Loaded, added.As<RebuildResponse>().Capacity).ShouldBe((i, 8));
        }

        h.Run(new CompletePalletRebuild { PalletNId = "EV-R1" }).StatusCode.ShouldBe(200);
        var rebuilt = Fg.Container(h, "EV-R1");
        (rebuilt.Status, rebuilt.Quantity, rebuilt.MaterialNId, rebuilt.EquipmentNId).ShouldBe((PalletStates.Full, 8m, "NV-FG-KIT-A", EquipmentLevels.NotApplicable));

        h.Ship(new StorePallet { PalletNId = "EV-R1", LocationNId = "NV1-CMN-FGW-A02" }, Shipper).StatusCode.ShouldBe(200);

        // Key tắt kiểm: pallet chưa đo cũng cất được.
        Fg.Pallet(h, "EV-P2", "NV-FG-KIT-A", totes: 8);
        h.Run(new SetConfigurationKey { NId = NvKeys.EolvValidation, Val = "false" }, app: SolutionManifest.FoundationAppName).StatusCode.ShouldBe(200);
        h.Ship(new StorePallet { PalletNId = "EV-P2", LocationNId = "NV1-CMN-FGW-A01" }, Shipper).StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Daily_retest_check_marks_pallets_whose_end_of_line_test_is_older_than_the_key_and_skips_without_the_key()
    {
        var h = await Fg.StackAsync();
        Fg.TestedPallet(h, "RT-P1");
        h.Read<SignalRule>(q => q.Where(r => r.NId == "EOLV_RETEST")).ShouldHaveSingleItem().Command.ShouldBe(nameof(CheckEolvRetest));

        h.Time.Advance(TimeSpan.FromDays(29));
        h.Ship(new CheckEolvRetest()).As<ShipResponse>().Quantity.ShouldBe(0);
        h.Time.Advance(TimeSpan.FromDays(2));
        h.Ship(new CheckEolvRetest()).As<ShipResponse>().Quantity.ShouldBe(1);
        Fg.Detail(h, "RT-P1").EolvExpired.ShouldBeTrue();

        // Thiếu key: lệnh vẫn thành công, lý do chỉ ở trace.
        h.Run(new SetConfigurationKey { NId = NvKeys.EolvRetestAfterDays, Val = "" }, app: SolutionManifest.FoundationAppName).StatusCode.ShouldBe(200);
        h.Ship(new CheckEolvRetest()).StatusCode.ShouldBe(200);
        h.Traces.Records.ShouldContain(r => r.Message.Contains($"{NvKeys.EolvRetestAfterDays} is missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pallet_checks_list_held_items_deep_in_the_tree_score_five_rules_and_block_the_qa_check()
    {
        var h = await Fg.StackAsync();
        Fg.Pallet(h, "QC-P1", "NV-FG-MOD-B60", totes: 6);
        // Một module có cây thành phần hai tầng: cell đã formation ← cell.
        h.Run(new CreateMtu { NId = "NV-WIP-CELL-B_QC-C1", MaterialNId = "NV-WIP-CELL-B" }).StatusCode.ShouldBe(200);
        h.Run(new CreateMtu { NId = "NV-WIP-FCELL-B_QC-C1", MaterialNId = "NV-WIP-FCELL-B" }).StatusCode.ShouldBe(200);
        Fg.Consume(h, "NV-FG-MOD-B60_QC-P1-K01", "NV-WIP-FCELL-B_QC-C1");
        Fg.Consume(h, "NV-WIP-FCELL-B_QC-C1", "NV-WIP-CELL-B_QC-C1");

        Fg.Held(h, "QC-P1").ShouldBeEmpty();
        h.Ship(new PalletStatusValidation { PalletNId = "QC-P1" }, Inspector).StatusCode.ShouldBe(200);
        var clean = Fg.Data(h, "QC-P1").ShouldHaveSingleItem();
        (clean.Quantity, clean.StandardQuantity, clean.Totes, clean.Items).ShouldBe((6m, 6, 6, 6));
        (clean.QuantityOk, clean.TotesOk, clean.ItemsOk, clean.MaterialOk, clean.AvailableOk).ShouldBe((true, true, true, true, true));

        h.Run(new CreateNonConformance { NId = "NC-QC-1", MtuNIds = ["NV-WIP-CELL-B_QC-C1"] }).StatusCode.ShouldBe(200);

        var held = Fg.Held(h, "QC-P1").ShouldHaveSingleItem();
        (held.MtuNId, held.Status, held.NonConformanceNId, held.NonConformanceStatus, held.Depth)
            .ShouldBe(("NV-WIP-CELL-B_QC-C1", MtuStatus.OnHold, "NC-QC-1", NonConformanceStatus.Open, 3));
        var refused = h.Ship(new PalletStatusValidation { PalletNId = "QC-P1" }, Inspector);
        Fg.Error(refused).ShouldBe(NvErrors.HeldOrScrapped);
        Fg.Message(refused).ShouldContain("1 item(s) on hold or scrapped");
        // Hàng trong tote vẫn Available: năm điều kiểm của pallet không thấy cell bị giữ ở tầng sâu.
        Fg.Data(h, "QC-P1").Single().AvailableOk.ShouldBeTrue();

        // Gỡ một tote bằng lệnh của lớp Product: lớp Product không đếm lại, nên số lượng vẫn bằng số chuẩn mà số tote lệch.
        h.Run(new RemoveFromMtua { ChildNId = "QC-P1-T01" }).StatusCode.ShouldBe(200);
        var short1 = Fg.Data(h, "QC-P1").Single();
        (short1.Quantity, short1.Totes, short1.QuantityOk, short1.TotesOk, short1.ItemsOk).ShouldBe((6m, 5, true, false, true));
    }

    [Fact]
    public async Task Clearing_a_pallet_moves_every_tote_out_and_forces_it_empty_without_moving_it()
    {
        var h = await Fg.StackAsync();
        Fg.Pallet(h, "CL-P1", "NV-FG-MOD-B60", totes: 6);
        h.Run(new MoveMtua { NId = "CL-P1", EquipmentNId = "NV1-CMN-FGW-B01" }).StatusCode.ShouldBe(200);

        var cleared = h.Ship(new UnloadTotesFromPallet { PalletNId = "CL-P1" }, Shipper);

        cleared.StatusCode.ShouldBe(200);
        var pallet = Fg.Container(h, "CL-P1");
        (pallet.Status, pallet.Quantity, pallet.MaterialNId, pallet.EquipmentNId).ShouldBe((PalletStates.Empty, 0m, null, "NV1-CMN-FGW-B01"));
        h.Read<Mtua>(q => q.Where(c => c.NId.StartsWith("CL-P1-T"))).ShouldAllBe(t => t.ParentMtuaNId == null && t.EquipmentNId == EquipmentLevels.NotApplicable);
    }

    [Fact]
    public async Task Loading_an_mtu_straight_onto_a_pallet_takes_its_totes_off_in_the_team_post_action()
    {
        var h = await Fg.StackAsync();
        Fg.Pallet(h, "DL-P1", "NV-FG-MOD-B60", totes: 6);
        h.Run(new CreateMtu { NId = "NV-FG-MOD-B60_DL-X", MaterialNId = "NV-FG-MOD-B60" }).StatusCode.ShouldBe(200);

        h.Run(new AssembleIntoMtua { ChildNId = "NV-FG-MOD-B60_DL-X", ContainerNId = "DL-P1" }).StatusCode.ShouldBe(200);

        h.Read<Mtua>(q => q.Where(c => c.ParentMtuaNId == "DL-P1")).ShouldBeEmpty();
        Fg.Container(h, "DL-P1").Quantity.ShouldBe(1m);
        h.Traces.Records.ShouldContain(r => r.Message.Contains("loaded straight onto DL-P1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Package_code_follows_the_keys_takes_one_material_up_to_the_standard_quantity_and_takes_the_tote_off_its_pallet()
    {
        var h = await Fg.StackAsync();
        Fg.Pallet(h, "CK-P1", "NV-FG-MOD-B60", totes: 6);

        Fg.Error(h.Ship(new OpenCkdPackage { PackageNId = "PK123" }, Shipper)).ShouldBe(NvErrors.InvalidPackageCode);
        Fg.Error(h.Ship(new OpenCkdPackage { PackageNId = "PK1234567X" }, Shipper)).ShouldBe(NvErrors.InvalidPackageCode);
        var opened = h.Ship(new OpenCkdPackage { PackageNId = "PK00000001" }, Shipper).As<ShipResponse>();
        (opened.Status, opened.Quantity).ShouldBe((PalletStates.Empty, 0m));

        h.Ship(new AddItemToCkdPackage { PackageNId = "PK00000001", MtuNId = "NV-FG-MOD-B60_CK-P1-K01" }, Shipper).As<ShipResponse>()
            .Status.ShouldBe(PalletStates.Full);
        h.Ship(new AddItemToCkdPackage { PackageNId = "PK00000001", MtuNId = "NV-FG-MOD-B60_CK-P1-K02" }, Shipper).StatusCode.ShouldBe(200);
        Fg.Error(h.Ship(new AddItemToCkdPackage { PackageNId = "PK00000001", MtuNId = "NV-FG-MOD-B60_CK-P1-K02" }, Shipper)).ShouldBe(NvErrors.AlreadyPacked);
        h.Run(new CreateMtu { NId = "NV-FG-MOD-B68_CK-Y", MaterialNId = "NV-FG-MOD-B68" }).StatusCode.ShouldBe(200);
        Fg.Error(h.Ship(new AddItemToCkdPackage { PackageNId = "PK00000001", MtuNId = "NV-FG-MOD-B68_CK-Y" }, Shipper)).ShouldBe(NvErrors.MaterialMismatch);

        var package = Fg.Container(h, "PK00000001");
        (package.Quantity, package.MaterialNId).ShouldBe((2m, "NV-FG-MOD-B60"));
        Fg.Container(h, "CK-P1").Quantity.ShouldBe(4m);
        Fg.Container(h, "CK-P1-T01").ParentMtuaNId.ShouldBeNull();

        h.Ship(new RemoveItemFromCkdPackage { PackageNId = "PK00000001", MtuNId = "NV-FG-MOD-B60_CK-P1-K02" }, Shipper).As<ShipResponse>()
            .Quantity.ShouldBe(1m);
        h.Ship(new RemoveItemFromCkdPackage { PackageNId = "PK00000001", MtuNId = "NV-FG-MOD-B60_CK-P1-K01" }, Shipper).As<ShipResponse>()
            .Status.ShouldBe(PalletStates.Empty);
    }

    [Fact]
    public async Task L03_suggestion_join_without_IsDeleted_lists_a_pallet_twice_once_an_older_pallet_with_its_code_was_deleted()
    {
        var h = await Fg.StackAsync();
        Fg.Store(h, Fg.Pallet(h, "L3-P1", "NV-FG-MOD-B60", totes: 6), "NV1-CMN-FGW-B01");
        Fg.Store(h, Fg.Pallet(h, "L3-P2", "NV-FG-MOD-B60", totes: 6), "NV1-CMN-FGW-B01");
        Fg.Suggested(h).Select(s => s.PalletNId).ShouldBe(["L3-P1", "L3-P2"]);

        // Pallet L3-P1 được gỡ, xóa mềm, rồi đóng lại với cùng mã và cất lại (bản ghi FIFO cùng mã được ghi đè).
        h.Ship(new UnloadTotesFromPallet { PalletNId = "L3-P1" }, Shipper).StatusCode.ShouldBe(200);
        h.Run(new DeleteMtua { NId = "L3-P1" }).StatusCode.ShouldBe(200);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        Fg.Store(h, Fg.Pallet(h, "L3-P1", "NV-FG-MOD-B60", totes: 6, prefix: "L3-P1B"), "NV1-CMN-FGW-B01");

        Fg.Suggested(h).Select(s => s.PalletNId).ShouldBe(["L3-P2", "L3-P1", "L3-P1"]);
        h.Read<FifoPallet>().Count.ShouldBe(2);

        // Đối chứng: cùng procedure có thêm điều kiện IsDeleted của bảng pallet thì mỗi pallet một dòng.
        var fixedSql = ReadingViews.Render(NvProcedures.Templates["nv_SuggestedPallets"], SolutionHarness.Registry)
            .Replace("ON pt.NId = fp.NId", "ON pt.NId = fp.NId AND pt.IsDeleted = 0", StringComparison.Ordinal);
        Fg.Sql(h, fixedSql);
        Fg.Suggested(h).Select(s => s.PalletNId).ShouldBe(["L3-P2", "L3-P1"]);
    }

    [Fact]
    public async Task L17_procedure_missing_after_deploy_fails_the_qa_check_until_the_manual_sql_step_runs()
    {
        var h = await Fg.StackAsync(sql: false);
        Fg.Pallet(h, "L17-P1", "NV-FG-MOD-B60", totes: 6);

        var failed = h.Ship(new PalletStatusValidation { PalletNId = "L17-P1" }, Inspector);

        failed.StatusCode.ShouldBe(400);
        Fg.Message(failed).ShouldContain("Could not find stored procedure 'nv_PalletHeldItems'");
        h.Read<Mtua>(q => q.Where(c => c.NId == "L17-P1")).ShouldHaveSingleItem();

        // Chạy lại Update Database không cài procedure; chỉ bước SQL tay mới cài.
        SchemaUpdater.UpdateDatabase(h.ConnectionString, SolutionHarness.Registry);
        Fg.Message(h.Ship(new PalletStatusValidation { PalletNId = "L17-P1" }, Inspector)).ShouldContain("Could not find stored procedure");
        ReadingViews.Install(h.ConnectionString, SolutionHarness.Registry).ShouldContain("nv_PalletHeldItems");
        h.Ship(new PalletStatusValidation { PalletNId = "L17-P1" }, Inspector).StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task L18_missing_connection_key_breaks_every_screen_and_command_that_calls_a_procedure()
    {
        var h = await Fg.StackAsync(key: false);
        Fg.Pallet(h, "L18-P1", "NV-FG-MOD-B60", totes: 6);

        Fg.Message(h.Ship(new PalletStatusValidation { PalletNId = "L18-P1" }, Inspector)).ShouldBe($"Configuration key {NvKeys.SqlConnection} is missing or empty.");
        Should.Throw<InvalidOperationException>(() => Fg.Held(h, "L18-P1")).Message.ShouldContain(NvKeys.SqlConnection);
        Should.Throw<InvalidOperationException>(() => Fg.Suggested(h)).Message.ShouldContain(NvKeys.SqlConnection);

        // Màn cất kho không gọi procedure nên vẫn chạy: thiếu key chỉ lộ ở màn và lệnh dùng procedure.
        h.Ship(new StorePallet { PalletNId = "L18-P1", LocationNId = "NV1-CMN-FGW-B01" }, Shipper).StatusCode.ShouldBe(200);

        h.Run(new SetConfigurationKey { NId = NvKeys.SqlConnection, Val = h.ConnectionString, Category = "FinishedGoods" },
            app: SolutionManifest.FoundationAppName).StatusCode.ShouldBe(200);
        h.Ship(new PalletStatusValidation { PalletNId = "L18-P1" }, Inspector).StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Held_items_procedure_keeps_pallet_and_tote_codes_in_16_characters_so_a_longer_tote_code_fails_with_truncation()
    {
        var h = await Fg.StackAsync();
        Fg.Pallet(h, "TR-P1", "NV-FG-MOD-B60", totes: 6, toteCode: i => $"TR-P1-LONGTOTE-{i:00}");

        var failed = h.Ship(new PalletStatusValidation { PalletNId = "TR-P1" }, Inspector);

        Fg.Message(failed).ShouldContain("truncated");
        Fg.Message(failed).ShouldContain("tote_nid");
    }
}

/// <summary>Dựng dữ liệu thành phẩm bằng command, như suite của team tạo pallet thử.</summary>
internal static class Fg
{
    public static readonly DateTimeOffset ShiftOpen = new(2026, 10, 26, 7, 1, 0, TimeSpan.Zero);

    /// <summary>Master data NV1, ca sáng 26/10 đã mở WO; cài SQL tay (view, procedure) và key kết nối nếu được yêu cầu.</summary>
    public static async Task<SolutionHarness> StackAsync(bool sql = true, bool key = true)
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        if (sql)
        {
            ReadingViews.Install(h.ConnectionString, SolutionHarness.Registry);
        }

        if (key)
        {
            h.Run(new SetConfigurationKey { NId = NvKeys.SqlConnection, Val = h.ConnectionString, Category = "FinishedGoods" },
                app: SolutionManifest.FoundationAppName).StatusCode.ShouldBe(200);
        }

        h.RunClockUntil(ShiftOpen);
        return h;
    }

    public static CommandResult Ship(this SolutionHarness h, object command, CallerIdentity? caller = null) => h.Run(command, caller, ShipApp.Name);

    public static CommandResult Yard(this SolutionHarness h, object command, CallerIdentity? caller = null) => h.Run(command, caller, YardApp.Name);

    public static int Error(CommandResult result) => result.Response?.Error?.ErrorCode ?? -1;

    public static string Message(CommandResult result) => result.Response?.Error?.ErrorMessage ?? result.Body.ToJsonString();

    public static Mtua Container(SolutionHarness h, string nid) => h.Read<Mtua>(q => q.Where(c => c.NId == nid)).Single();

    public static PackingDetail Detail(SolutionHarness h, string pallet)
    {
        var id = Container(h, pallet).Id;
        return h.Read<PackingDetail>(q => q.Where(d => d.EntityId == id)).Single();
    }

    /// <summary>
    /// Pallet <paramref name="pallet"/> với <paramref name="totes"/> tote, mỗi tote một sản phẩm <c>&lt;material&gt;_&lt;prefix&gt;-Kxx</c>
    /// mang serial (như hàng ra từ chuyền); xếp xong thì Full.
    /// </summary>
    public static string Pallet(SolutionHarness h, string pallet, string material, int totes, string? prefix = null, Func<int, string>? toteCode = null)
    {
        prefix ??= pallet;
        Ok(h.Run(new CreateMtua { NId = pallet, ContainerType = ContainerTypes.Pallet, StateMachine = NvStateMachines.Pallet, Status = PalletStates.Empty }));
        for (var i = 1; i <= totes; i++)
        {
            var serial = $"{prefix}-K{i:00}";
            var tote = toteCode?.Invoke(i) ?? $"{prefix}-T{i:00}";
            Ok(h.Run(new CreateMtu { NId = material + "_" + serial, MaterialNId = material, Code = serial }));
            Ok(h.Run(new CreateMtua { NId = tote, ContainerType = ContainerTypes.Tote, StateMachine = NvStateMachines.Tote, Status = ToteStates.New }));
            Ok(h.Run(new AssembleIntoMtua { ChildNId = material + "_" + serial, ContainerNId = tote }));
            Ok(h.Run(new AssembleIntoMtua { ChildNId = tote, ContainerNId = pallet }));
        }

        Ok(h.Run(new SetMtuaStatus { NId = pallet, Status = PalletStates.Full }));
        return pallet;
    }

    /// <summary>Pallet họ A đã đo cuối: dựng, đo từng kit (tote rời pallet), dựng lại cùng mã.</summary>
    public static string TestedPallet(SolutionHarness h, string pallet)
    {
        Pallet(h, pallet, "NV-FG-KIT-A", totes: 8);
        Test(h, [.. Enumerable.Range(1, 8).Select(i => $"{pallet}-K{i:00}")]).NotExecutedOperations.ShouldBeEmpty();
        Ok(h.Run(new StartPalletRebuild { PalletNId = pallet }));
        for (var i = 1; i <= 8; i++)
        {
            Ok(h.Run(new AddToteToRebuiltPallet { PalletNId = pallet, ToteNId = $"{pallet}-T{i:00}" }));
        }

        Ok(h.Run(new CompletePalletRebuild { PalletNId = pallet }));
        return pallet;
    }

    /// <summary>Tin đo cuối (OK) của các kit trên line 1, như máy đo gửi.</summary>
    public static ApplyLineResultsResponse Test(SolutionHarness h, IReadOnlyList<string> kits)
    {
        var result = h.Run(new ApplyLineResults
        {
            Operations = [.. kits.Select(k => new LineOperationDetail
            {
                OperationCode = "eo010", EquipmentCode = "eo010_m1_1", Serial = k, StatusId = "OK", StatusCode = "00",
                EndTime = h.Time.GetUtcNow().AddSeconds(-30),
            })],
        });
        result.StatusCode.ShouldBe(200);
        return result.As<ApplyLineResultsResponse>();
    }

    public static void Consume(SolutionHarness h, string product, string component)
    {
        var workOrder = h.Read<WorkOrder>(q => q.Where(w => w.Status == WorkOrderStates.Active)).First().NId;
        Ok(h.Run(new RecordConsumption { WorkOrderNId = workOrder, ProductMtuNId = product, ComponentMtuNId = component, Quantity = 1m }));
    }

    public static void Store(SolutionHarness h, string pallet, string location)
    {
        Ok(h.Ship(new StorePallet { PalletNId = pallet, LocationNId = location }));
        h.DeliverEvents();
    }

    public static List<PalletHeldItem> Held(SolutionHarness h, string pallet) =>
        Read<PalletHeldItems, PalletHeldItem>(h, new PalletParameters { PalletNId = pallet });

    public static List<PalletDataRow> Data(SolutionHarness h, string pallet) =>
        Read<PalletDataCheck, PalletDataRow>(h, new PalletDataParameters { PalletNId = pallet });

    public static List<SuggestedPalletRow> Suggested(SolutionHarness h, string material = "NV-FG-MOD-B60") =>
        Read<SuggestedPallets, SuggestedPalletRow>(h, new MaterialParameters { MaterialNId = material });

    public static void Sql(SolutionHarness h, string sql)
    {
        using var connection = new SqlConnection(h.ConnectionString);
        connection.Open();
#pragma warning disable CA2100 // SQL của chính test
        using var command = new SqlCommand(sql, connection);
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static List<TRow> Read<TFunction, TRow>(SolutionHarness h, object parameters)
        where TFunction : class
    {
        using var session = h.Executor.OpenRead();
        var function = SolutionHarness.Registry.ReadingFunctionByPublicName(ShipApp.Name, typeof(TFunction).Name)!;
        var json = System.Text.Json.JsonSerializer.SerializeToElement(parameters, parameters.GetType());
        return [.. session.Invoke(function, json).Cast<TRow>()];
    }

    private static void Ok(CommandResult result) => result.StatusCode.ShouldBe(200, result.Body.ToJsonString());
}
