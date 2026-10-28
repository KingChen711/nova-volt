using Npgsql;
using Nvm.Product;
using Nvm.Project;
using Nvm.Shopfloor;

namespace Nvm.SolutionTests;

/// <summary>Lab sự cố của R5 (tin từ xưởng): tái hiện, triệu chứng, chẩn đoán, cách sửa, đối chứng.</summary>
public sealed class LineLabTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 6, 10, 0, TimeSpan.Zero);

    /// <summary>Khung ca S2 của khu điện cực line 1 bị khai hẹp lại: 06:10 không còn nằm trong ca nào, chưa có WO nào mở.</summary>
    private static void NarrowNightShift(SolutionHarness mom, string end = "23:59") =>
        mom.Run(new CreateProductionSchedule
        {
            NId = "PS_NV1-B1-L1-ELEC-S2",
            AreaEquipmentNId = "NV1-B1-L1-ELEC",
            Shift = "S2",
            StartTime = "19:00",
            EndTime = end,
        }).StatusCode.ShouldBe(200);

    private static void RestoreNightShift(SolutionHarness mom) => NarrowNightShift(mom, "06:59");

    [Fact]
    public async Task L09_succeeded_response_still_lists_the_serial_as_not_executed()
    {
        var mom = await SolutionHarness.WithMasterDataAsync();
        NarrowNightShift(mom);
        var lead = SolutionHarness.Caller("lead.nv1", "line-lead");
        var message = new ApplyLineResults
        {
            Operations = [new LineOperationDetail { OperationCode = "el010", EquipmentCode = "el010_m1_1", Serial = "L09-COIL", Quantity = 100, StatusId = "OK", StatusCode = "00" }],
        };

        // Triệu chứng: HTTP 200, Succeeded, mà serial nằm ở danh sách chưa ghi.
        var result = mom.Run(message, lead);
        result.StatusCode.ShouldBe(200);
        var response = result.As<ApplyLineResultsResponse>();
        response.Succeeded.ShouldBeTrue();
        response.ExecutedSerialNumbers.ShouldBeEmpty();
        response.NotExecutedOperations.ShouldHaveSingleItem().Error.Code.ShouldBe(LineErrors.OperationNotFound);

        // Chẩn đoán: trace của lời gọi gãy ở bước mở WO dự phòng; OData đếm 0 WO cho process.
        mom.Traces.ByCorrelation(result.CorrelationId)
            .ShouldContain(r => r.Message.StartsWith("Matching BoP - Process relationship could not be found for Coat_LN1", StringComparison.Ordinal));
        mom.Read<WorkOrder>(q => q.Where(w => w.ProcessNId == "Coat_LN1")).ShouldBeEmpty();

        // Sửa đúng một biến (khung ca), gọi lại: WO dự phòng được mở, serial được ghi.
        RestoreNightShift(mom);
        var again = mom.Run(message, lead).As<ApplyLineResultsResponse>();
        again.ExecutedSerialNumbers.ShouldBe(["L09-COIL"]);
        mom.Read<WorkOrder>(q => q.Where(w => w.ProcessNId == "Coat_LN1")).ShouldHaveSingleItem().Status.ShouldBe(WorkOrderStates.Active);
        mom.Read<Mtu>(q => q.Where(m => m.Code == "L09-COIL")).ShouldHaveSingleItem().NId.ShouldBe("NV-WIP-ELEC-A_L09-COIL");
    }

    [Fact]
    public async Task L10_rows_rejected_before_the_fix_stay_on_the_relay_path_until_set_back_to_new()
    {
        var line = await LineStack.CreateAsync(transfer: "relay", start: Start);
        NarrowNightShift(line.Mom);
        line.Simulate(new LineScenario { Line = 1, Start = Start, Coils = 2, Cells = 0 });
        await using var listener = line.Listener();
        await listener.StartAsync(TestContext.Current.CancellationToken);

        line.SendPending().ShouldBe(2);
        await listener.DrainAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        line.Mom.DeliverEvents();
        line.Row("SL1-EC001", "el010").ShouldBe((LineRowStatus.Retry, -6200, 1));

        // Sửa nguyên nhân: tin cũ không tự vào, vì -6200 không có dòng chính sách và đường relay chỉ gửi lại dòng có chính sách.
        RestoreNightShift(line.Mom);
        line.Mom.Time.Advance(TimeSpan.FromMinutes(15));
        line.SendPending().ShouldBe(0);
        line.Row("SL1-EC001", "el010").ShouldBe((LineRowStatus.Retry, -6200, 1));
        line.Scalar<long>("SELECT count(*) FROM relay.retry_policy WHERE error_code = -6200;").ShouldBe(0L);

        // Gửi lại bằng tay theo mẫu của team: đếm, đặt về NEW, rồi đếm lại tới khi về DONE.
        line.Resend("s.status = 'RETRY' AND s.error_code = -6200").ShouldBe(2);
        line.SendPending().ShouldBe(2);
        await listener.DrainAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        line.Mom.DeliverEvents();
        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.Done, 0)] = 2 });
    }

    [Fact]
    public async Task L10_control_poll_path_resends_a_row_without_policy_once_after_ten_minutes_then_stands_still()
    {
        var line = await LineStack.CreateAsync(start: Start);
        NarrowNightShift(line.Mom);
        line.Simulate(new LineScenario { Line = 1, Start = Start, Coils = 1, Cells = 0 });

        line.Poll("el010");
        line.Row("SL1-EC001", "el010").ShouldBe((LineRowStatus.Retry, -6200, 1));

        // Chưa tới 10 phút: chưa gửi lại. Đủ 10 phút: gửi lại một lần (vẫn lỗi vì chưa sửa).
        line.Mom.Time.Advance(TimeSpan.FromMinutes(9));
        line.Poll("el010").ShouldBe(0);
        line.Mom.Time.Advance(TimeSpan.FromMinutes(1));
        line.Poll("el010").ShouldBe(1);
        line.Row("SL1-EC001", "el010").ShouldBe((LineRowStatus.Retry, -6200, 2));

        // Hết lượt mặc định: sửa xong dòng vẫn đứng yên tới khi đặt về NEW.
        RestoreNightShift(line.Mom);
        line.Mom.Time.Advance(TimeSpan.FromMinutes(30));
        line.Poll("el010").ShouldBe(0);
        line.Resend("s.status = 'RETRY'").ShouldBe(1);
        line.Poll("el010").ShouldBe(1);
        line.Row("SL1-EC001", "el010").ShouldBe((LineRowStatus.Done, 0, 1));
    }

    [Fact]
    public async Task Poll_path_recovers_by_itself_when_the_cause_is_fixed_within_the_retry_window()
    {
        var line = await LineStack.CreateAsync(start: Start);
        NarrowNightShift(line.Mom);
        line.Simulate(new LineScenario { Line = 1, Start = Start, Coils = 1, Cells = 0 });
        line.Poll("el010");

        RestoreNightShift(line.Mom);
        line.Mom.Time.Advance(TimeSpan.FromMinutes(10));
        line.Poll("el010");

        line.Row("SL1-EC001", "el010").ShouldBe((LineRowStatus.Done, 0, 2));
    }

    [Fact]
    public async Task L11_payload_over_the_notify_limit_marks_rows_notify_err_and_they_are_never_resent()
    {
        var line = await LineStack.CreateAsync(transfer: "relay");
        InsertModules(line.Owner, count: 3, cellsPerModule: LineSimulator.CellsPerModule(4));
        await using var listener = line.Listener();
        await listener.StartAsync(TestContext.Current.CancellationToken);

        // Triệu chứng: lịch chạy, không lỗi nào lên MOM, không event nào; LineDB ghi NOTIFY_ERR.
        line.SendPending(rowsPerMessage: 5).ShouldBe(0);
        (await listener.DrainAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).ShouldBeFalse();
        line.Statuses().ShouldBe(new Dictionary<(string, int), int> { [(LineRowStatus.NotifyError, 0)] = 3 });

        // Không tự gửi lại, kể cả nhiều giờ sau.
        line.Mom.Time.Advance(TimeSpan.FromHours(2));
        line.SendPending(rowsPerMessage: 5).ShouldBe(0);

        // Chẩn đoán: một dòng module họ B 68 cell đã gần nửa giới hạn 8000 byte; gom 3 dòng là vượt.
        line.Scalar<int>("SELECT max(octet_length(payload::text)) FROM relay.line_message;").ShouldBeGreaterThan(3000);

        // Sửa: gom ít dòng hơn mỗi notify, đặt các dòng về NEW. Đối chứng: cùng dữ liệu, từng dòng một thì đi được.
        line.Resend("s.status = 'NOTIFY_ERR'").ShouldBe(3);
        line.SendPending(rowsPerMessage: 1).ShouldBe(3);
        (await listener.DrainAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeTrue();
        line.Mom.DeliverEvents().ShouldBe(3);
    }

    private static void InsertModules(string owner, int count, int cellsPerModule)
    {
        using var connection = new NpgsqlConnection(owner);
        connection.Open();
        for (var m = 1; m <= count; m++)
        {
            using var insert = new NpgsqlCommand("""
                INSERT INTO mod.module_result (line_no, machine, op_code, module_id, cell_item, cell_ids, started_at, finished_at, result, result_code)
                VALUES (4, 'mo010_m1', 'mo010', @module, 'Formed cell B in use', @cells, @at, @at, 'OK', '00');
                """, connection);
            insert.Parameters.AddWithValue("module", $"L11-M{m:000}");
            insert.Parameters.AddWithValue("cells", Enumerable.Range(1, cellsPerModule).Select(c => $"L11-M{m:000}-C{c:0000}").ToArray());
            insert.Parameters.AddWithValue("at", Start.ToUniversalTime());
            insert.ExecuteNonQuery();
        }
    }
}
