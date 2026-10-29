using Npgsql;
using Nvm.Product;
using Nvm.Project;
using Nvm.Shopfloor;
using Nvm.Shopfloor.LineDb;
using Nvm.Solution.Suites;

namespace Nvm.SolutionTests;

/// <summary>DoD R5: tin từ xưởng đi qua LineDB (Postgres thật) vào MOM, theo cả hai đường và hai chỗ lưu trạng thái.</summary>
public sealed class LineMessageTests
{
    private static readonly DateTimeOffset ShiftStart = new(2026, 10, 26, 7, 5, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(LineStateStorage.Table, "qa")]
    [InlineData(LineStateStorage.Columns, "prod")]
    public async Task One_shift_on_one_line_records_exactly_the_scenario(LineStateStorage storage, string environment)
    {
        var line = await LineStack.CreateAsync(storage: storage, environment: environment);
        line.OpenDayShift();
        var expected = line.Simulate(new LineScenario { Line = 1, Start = ShiftStart, Coils = 2, Cells = 24 });

        line.Poll();

        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.Done, 0)] = expected.UnitRows });
        var mom = line.Mom;
        Count(mom, "NV-WIP-ELEC-A_").ShouldBe(expected.Coils);
        Count(mom, "NV-WIP-CELL-A_").ShouldBe(expected.Cells);
        Count(mom, "NV-WIP-FCELL-A_").ShouldBe(expected.FormedCells);
        Count(mom, "NV-FG-MOD-A_").ShouldBe(expected.Modules);
        Count(mom, "Auto_").ShouldBe(expected.TemporaryMtus);
        (Count(mom, "NV-RM-ANODE_") + Count(mom, "NV-RM-CATHODE_")).ShouldBe(expected.LotMtus);

        mom.Read<MaterialConsumption>().Count.ShouldBe(expected.Consumptions);
        mom.Read<MtuHistory>(q => q.Where(h => h.Action == MtuHistoryActions.Assemble && h.ComponentMtuNId != null)).Count.ShouldBe(expected.Consumptions);
        mom.Read<LineExecution>().Count.ShouldBe(expected.UnitRows);

        Produced(mom, "Coat_LN1").ShouldBe(expected.CoatedLength);
        Produced(mom, "Assy_LN1").ShouldBe(expected.Cells);
        Produced(mom, "Form_LN1").ShouldBe(expected.FormedCells);
        Produced(mom, "Module_LN1").ShouldBe(expected.Modules);

        // Cuộn đầu tiên nuôi cả 24 cell, mỗi cell 1,2 m theo định mức; cell đã vào formation, cell đã formation đã vào module.
        mom.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-ELEC-A_SL1-EC001")).Single().Quantity.ShouldBe(120m - (24 * 1.2m));
        mom.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-CELL-A_SL1-C0001")).Single().Quantity.ShouldBe(0m);
        mom.Read<Mtu>(q => q.Where(m => m.NId == "NV-WIP-FCELL-A_SL1-C0001")).Single().Quantity.ShouldBe(0m);
    }

    [Fact]
    public async Task Rows_sent_again_do_not_create_or_consume_twice()
    {
        var line = await LineStack.CreateAsync();
        line.OpenDayShift();
        var expected = line.Simulate(new LineScenario { Line = 2, Start = ShiftStart, Coils = 1, Cells = 12 });
        line.Poll();
        var lot = line.Mom.Read<Mtu>(q => q.Where(m => m.NId == "NV-RM-ANODE_AN-SL2-001")).Single().Quantity;

        // Đặt tất cả về NEW như một lần gửi lại bằng tay, rồi đọc lại toàn bộ.
        line.Resend("true").ShouldBe(expected.UnitRows);
        line.Poll();

        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.Done, 0)] = expected.UnitRows });
        line.Mom.Read<MaterialConsumption>().Count.ShouldBe(expected.Consumptions);
        line.Mom.Read<LineExecution>().Count.ShouldBe(expected.UnitRows);
        Produced(line.Mom, "Assy_LN2").ShouldBe(expected.Cells);
        line.Mom.Read<Mtu>(q => q.Where(m => m.NId == "NV-RM-ANODE_AN-SL2-001")).Single().Quantity.ShouldBe(lot);
        line.Mom.Traces.Records.ShouldContain(r => r.Message.Contains("is already recorded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ko_cells_get_a_non_conformance_and_shrink_rows_split_the_lot_under_one()
    {
        var line = await LineStack.CreateAsync();
        line.OpenDayShift();
        var expected = line.Simulate(new LineScenario { Line = 1, Start = ShiftStart, Coils = 1, Cells = 16, KoEvery = 4, ShrinkLogs = 2 });

        line.Poll();

        // R6: tin hao hụt không còn dừng ở -5900; cấu hình hủy theo ca không nạp ở đây nên NC là NC chung OPEN.
        expected.KoCells.ShouldBe(4);
        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.Done, 0)] = expected.UnitRows + expected.ShrinkRows });
        line.Mom.Read<LineExecution>(q => q.Where(e => e.StatusId == "KO")).Count.ShouldBe(expected.KoCells);
        Count(line.Mom, "NV-WIP-FCELL-A_").ShouldBe(12);
        var details = line.Mom.Read<NonConformanceDetail>();
        details.Count(d => d.Origin == NonConformanceOrigins.Line).ShouldBe(expected.KoCells);
        details.Count(d => d.Origin == NonConformanceOrigins.Shrink).ShouldBe(expected.ShrinkRows);
        line.Mom.Read<LineShrinkExecution>().Count.ShouldBe(expected.ShrinkRows);
    }

    [Fact]
    public async Task Ko_serials_come_back_in_the_pending_list_of_the_response()
    {
        var line = await LineStack.CreateAsync();
        line.OpenDayShift();
        var result = line.Mom.Run(new ApplyLineResults
        {
            Operations =
            [
                new LineOperationDetail { OperationCode = "el010", EquipmentCode = "el010_m1_1", Serial = "KO-COIL", Quantity = 50, StatusId = "KO", StatusCode = "31" },
            ],
        }, SolutionHarness.Caller("lead.nv1", "line-lead"));

        result.StatusCode.ShouldBe(200);
        var response = result.As<ApplyLineResultsResponse>();
        response.ExecutedSerialNumbers.ShouldBe(["KO-COIL"]);
        response.PendingNonConformance.ShouldBe(["KO-COIL"]);
    }

    [Fact]
    public async Task Unknown_machine_code_is_not_executed_with_its_composite_code()
    {
        var line = await LineStack.CreateAsync();
        line.OpenDayShift();
        await using (var connection = new NpgsqlConnection(line.Owner))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO elec.coater_result (line_no, machine, op_code, coil_id, length_m, started_at, finished_at, result, result_code)
                VALUES (1, 'el010_m9', 'el010', 'COIL-X', 100, @at, @at, 'OK', '00');
                """, connection);
            insert.Parameters.AddWithValue("at", ShiftStart.ToUniversalTime());
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        line.Poll();

        line.Row("COIL-X", "el010").ShouldBe((LineRowStatus.Retry, -6120, 1));
    }

    [Fact]
    public async Task Relay_path_drains_the_backlog_after_mom_was_down_without_loss_or_duplicates()
    {
        var line = await LineStack.CreateAsync(transfer: "relay");
        line.OpenDayShift();
        var expected = line.Simulate(new LineScenario { Line = 1, Start = ShiftStart, Coils = 1, Cells = 12 });

        // MOM dừng: lịch phía xưởng vẫn notify, không ai nghe, tin mất; mọi dòng nằm ở SENT.
        line.SendPending().ShouldBe(expected.UnitRows);
        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.Sent, 0)] = expected.UnitRows });

        // 30 phút sau MOM chạy lại: bên nghe lên trước, nhịp kế của lịch gửi lại dòng SENT theo chính sách.
        line.Mom.Time.Advance(TimeSpan.FromMinutes(30));
        await using var listener = line.Listener();
        await listener.StartAsync(TestContext.Current.CancellationToken);
        line.SendPending().ShouldBe(expected.UnitRows);
        (await listener.DrainAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeTrue();
        line.Mom.DeliverEvents().ShouldBeGreaterThan(0);

        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.Done, 0)] = expected.UnitRows });
        line.Mom.Read<LineExecution>().Count.ShouldBe(expected.UnitRows);
        line.Mom.Read<MaterialConsumption>().Count.ShouldBe(expected.Consumptions);

        // Nhịp sau không còn gì để gửi.
        line.Mom.Time.Advance(TimeSpan.FromMinutes(15));
        line.SendPending().ShouldBe(0);
    }

    [Fact]
    public async Task Poll_path_waits_while_mom_is_down_and_reads_everything_once_it_is_back()
    {
        var line = await LineStack.CreateAsync();
        line.OpenDayShift();
        var expected = line.Simulate(new LineScenario { Line = 3, Start = ShiftStart, Coils = 1, Cells = 60 });

        // MOM dừng 30 phút: không signal rule nào chạy, dòng ở NEW.
        line.Mom.Time.Advance(TimeSpan.FromMinutes(30));
        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.New, 0)] = expected.UnitRows });

        line.Poll();

        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.Done, 0)] = expected.UnitRows });
        Count(line.Mom, "NV-FG-MOD-B60_").ShouldBe(1);
        line.Mom.Read<MaterialConsumption>().Count.ShouldBe(expected.Consumptions);
    }

    [Fact]
    public async Task Signal_rules_of_the_poll_deploy_step_read_line_rows_every_minute()
    {
        var line = await LineStack.CreateAsync(transfer: "relay");
        line.OpenDayShift();
        var history = Path.Combine(Path.GetTempPath(), "nvl-" + Guid.NewGuid().ToString("N") + ".txt");
        MasterDataDeployment.Run(Path.Combine(SolutionHarness.SuitesRoot, "line-polling"), line.Mom.Target(), history)
            .ShouldAllBe(s => s.Status == SuiteStatus.Executed);
        line.Simulate(new LineScenario { Line = 1, Start = ShiftStart, Coils = 1, Cells = 0 });

        for (var minute = 0; minute < 2; minute++)
        {
            line.Mom.Time.Advance(TimeSpan.FromMinutes(1));
            line.Mom.Rules.RunDueTimers();
            line.Mom.DeliverEvents();
        }

        line.Row("SL1-EC001", "el010").ShouldBe((LineRowStatus.Done, 0, 1));
    }

    [Fact]
    public async Task Relay_is_the_default_path_and_the_read_command_reads_nothing_then()
    {
        var line = await LineStack.CreateAsync(transfer: "relay");
        line.OpenDayShift();
        line.Simulate(new LineScenario { Line = 1, Start = ShiftStart, Coils = 1, Cells = 0 });

        line.Poll().ShouldBe(0);

        line.Row("SL1-EC001", "el010").ShouldBe((LineRowStatus.New, 0, 0));
    }

    [Fact]
    public async Task Mom_role_reaches_only_the_relay_functions()
    {
        var line = await LineStack.CreateAsync();
        line.Simulate(new LineScenario { Line = 1, Start = ShiftStart, Coils = 1, Cells = 0 });
        await using var connection = new NpgsqlConnection(line.MomDb);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        foreach (var table in new[] { "elec.coater_result", "relay.line_message", "relay.message_status" })
        {
#pragma warning disable CA2100 // tên bảng là hằng của test
            await using var read = new NpgsqlCommand($"SELECT count(*) FROM {table};", connection);
#pragma warning restore CA2100
            var denied = await Should.ThrowAsync<PostgresException>(() => read.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        await using var claim = new NpgsqlCommand("SELECT count(*) FROM relay.claim('el010', 10, 'poll', now());", connection);
        (await claim.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1L);
    }

    private static int Count(SolutionHarness mom, string prefix) => mom.Read<Mtu>(q => q.Where(m => m.NId.StartsWith(prefix))).Count;

    private static decimal Produced(SolutionHarness mom, string process) =>
        mom.Read<WorkOrder>(q => q.Where(w => w.ProcessNId == process)).Sum(w => w.ProducedQuantity);
}
