using Nvm.Product;
using Nvm.Project;
using Nvm.Shopfloor;

namespace Nvm.SolutionTests;

/// <summary>
/// DoD R7: chuỗi tin của một line qua LineDB (Postgres thật) ra tote và pallet Full đúng số tote, theo bảng cấu hình bước đóng gói.
/// </summary>
public sealed class PackingLineTests
{
    private static readonly DateTimeOffset ShiftStart = new(2026, 10, 26, 7, 5, 0, TimeSpan.Zero);

    /// <summary>Mã bước theo thứ tự chuỗi, kể cả đóng tote (họ A, họ B), xếp pallet, đo cuối.</summary>
    private static readonly string[] Chain = [.. LineStack.Operations, "mo020", "mo025", "mo030", "eo010"];

    [Theory]
    [InlineData(1, 192, "NV-FG-KIT-A", 8)]
    [InlineData(3, 360, "NV-FG-MOD-B60", 6)]
    public async Task One_shift_of_line_messages_packs_totes_and_a_full_pallet_with_the_standard_number_of_totes(int lineNo, int cells,
        string product, int totes)
    {
        var line = await LineStack.CreateAsync();
        line.OpenDayShift();
        var expected = line.Simulate(new LineScenario { Line = lineNo, Start = ShiftStart, Coils = (cells + 99) / 100, Cells = cells, Pack = true });
        (expected.Totes, expected.Pallets, expected.TotesPerPallet).ShouldBe((totes, 1, totes));

        line.Poll(Chain);

        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.Done, 0)] = expected.UnitRows });
        var mom = line.Mom;
        var pallet = mom.Read<Mtua>(q => q.Where(c => c.NId == $"SL{lineNo}-P01")).Single();
        (pallet.ContainerType, pallet.Status, pallet.Quantity, pallet.MaterialNId).ShouldBe((ContainerTypes.Pallet, PalletStates.Full, (decimal)totes, product));
        mom.Read<PackingDetail>(q => q.Where(d => d.EntityId == pallet.Id)).Single().BuildDate.ShouldNotBeNull();

        var onPallet = mom.Read<Mtua>(q => q.Where(c => c.ParentMtuaNId == pallet.NId));
        onPallet.Count.ShouldBe(totes);
        onPallet.ShouldAllBe(t => t.ContainerType == ContainerTypes.Tote && t.Status == ToteStates.New && t.Quantity == 1m && t.MaterialNId == product);
        var toteIds = onPallet.Select(t => t.NId).ToList();
        var items = mom.Read<Mtu>(q => q.Where(m => toteIds.Contains(m.ParentMtuaNId!)));
        items.Count.ShouldBe(totes);
        items.ShouldAllBe(m => m.MaterialNId == product && m.Status == MtuStatus.Available && m.Quantity == 1m);

        if (lineNo == 1)
        {
            // Họ A: kit là sản phẩm của WO Kit_LN1, ghép hai module; module đã dùng còn lượng 0.
            Count(mom, "NV-FG-KIT-A_").ShouldBe(expected.Kits);
            mom.Read<WorkOrder>(q => q.Where(w => w.ProcessNId == "Kit_LN1" && w.Status == WorkOrderStates.Active)).Single().ProducedQuantity
                .ShouldBe(expected.Kits);
            mom.Read<Mtu>(q => q.Where(m => m.NId == "NV-FG-MOD-A_SL1-M001")).Single().Quantity.ShouldBe(0m);
        }

        mom.Read<MaterialConsumption>().Count.ShouldBe(expected.Consumptions);
        mom.Read<LineExecution>().Count.ShouldBe(expected.UnitRows);
        mom.Read<LineExecution>(q => q.Where(e => e.OperationCode == "mo030")).Single().WorkOrderNId.ShouldBeNull();
    }

    [Fact]
    public async Task Pallet_message_before_its_tote_messages_waits_with_its_packing_code_and_goes_through_on_the_resend()
    {
        var line = await LineStack.CreateAsync();
        line.OpenDayShift();
        var expected = line.Simulate(new LineScenario { Line = 3, Start = ShiftStart, Coils = 4, Cells = 360, Pack = true });

        // Đường poll đọc theo mã bước: tin xếp pallet có thể được đọc trước tin đóng tote.
        line.Poll("mo030");
        line.Row("SL3-P01", "mo030").ShouldBe((LineRowStatus.Retry, LineErrors.Composite(LineProcessType.Apply, LineErrors.PackingFailed), 1));
        line.Mom.Read<Mtua>(q => q.Where(c => c.NId == "SL3-P01")).ShouldBeEmpty();

        line.Poll(LineStack.Operations);
        line.Poll("mo025");
        line.Mom.Time.Advance(TimeSpan.FromMinutes(11));
        line.Poll("mo030");

        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.Done, 0)] = expected.UnitRows });
        line.Mom.Read<Mtua>(q => q.Where(c => c.NId == "SL3-P01")).Single().Quantity.ShouldBe(6m);
    }

    [Fact]
    public async Task End_of_line_test_messages_take_each_tested_tote_off_the_pallet_and_leave_it_empty_where_it_stood()
    {
        var line = await LineStack.CreateAsync();
        line.OpenDayShift();
        var expected = line.Simulate(new LineScenario { Line = 1, Start = ShiftStart, Coils = 2, Cells = 192, Pack = true, EolvTest = true });

        line.Poll(Chain);

        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.Done, 0)] = expected.UnitRows });
        expected.EolvRows.ShouldBe(8);
        var pallet = line.Mom.Read<Mtua>(q => q.Where(c => c.NId == "SL1-P01")).Single();
        (pallet.Status, pallet.Quantity, pallet.MaterialNId, pallet.EquipmentNId).ShouldBe((PalletStates.Empty, 0m, null, "NV1-B1-L1-MOD-PACK-OP30-M1"));
        line.Mom.Read<Mtua>(q => q.Where(c => c.NId.StartsWith("SL1-T"))).ShouldAllBe(t => t.ParentMtuaNId == null && t.EquipmentNId == "NV1-B1-L1-MOD-EOLV-OP40-M1");
        line.Mom.Read<WorkOrder>(q => q.Where(w => w.ProcessNId == "Eolv_LN1" && w.Status == WorkOrderStates.Active)).Single().ProducedQuantity.ShouldBe(8m);
    }

    private static int Count(SolutionHarness mom, string prefix) => mom.Read<Mtu>(q => q.Where(m => m.NId.StartsWith(prefix))).Count;
}
