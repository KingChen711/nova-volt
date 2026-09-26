using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nvm.CommandStore;
using Nvm.Equipment.Commands;
using Nvm.Equipment.Entities;
using Nvm.Equipment.Hosting;
using Nvm.EventStore;
using Nvm.Kernel.Commands;

namespace Nvm.IntegrationTests;

/// <summary>M10: dừng máy có lý do lá, micro-stop, OEE hai line gộp bằng base và khớp ground truth tính tay.</summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class EquipmentOeeTests(SqlCommandStoreFixture sql) : IClassFixture<SqlCommandStoreFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 12, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string L1 = "NOVAVOLT/NV1/CELL/L1/STACK-01";
    private const string L2 = "NOVAVOLT/NV1/CELL/L2/STACK-01";
    private const string Product = "NV-P120-NMC";

    [Fact]
    public async Task TwoLines_CombinedOeeIsComputedFromSummedBases_AndMatchesHandCalculatedGroundTruth()
    {
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        var clock = new FakeTimeProvider(T0.AddHours(9));
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock);
        (await host.DispatchAsync(new RecordProductionCountCommand("NV1", "plc", "count-before-master", T0, L1, Product, T0,
            T0.AddHours(1), 1, 1), Ct)).ReasonCode.ShouldBe(EquipmentReasonCodes.EquipmentNotFound);
        await Ok(host, new RegisterEquipmentCommand("NV1", "admin", "reg-l1", T0, L1, "STACKER"));
        await Ok(host, new RegisterEquipmentCommand("NV1", "admin", "reg-l2", T0, L2, "STACKER"));
        (await Count(host, L1, 0, 4, 10_000, 9_500)).ReasonCode.ShouldBe(EquipmentReasonCodes.NoIdealCycle);
        (await host.DispatchAsync(new SetIdealCycleTimeCommand("NV1", "pm", "ideal-v1", T0, "STACKER", Product, 1_000,
            T0.AddDays(-1)), Ct)).ReasonText.ShouldBe($"STACKER/{Product} v1");

        // Line 1 chạy cả 8 giờ: đổi sản phẩm 1 giờ (kế hoạch), hỏng 30 phút, kẹt 3 phút (micro-stop).
        (await State(host, L1, 1, 0, EquipmentStates.Stopped, "MECHANICAL")).ReasonCode.ShouldBe(EquipmentReasonCodes.ReasonNotLeaf);
        (await State(host, L1, 1, 0, EquipmentStates.Stopped, "NOT_A_REASON")).ReasonCode.ShouldBe(EquipmentReasonCodes.UnknownReason);
        await Ok(host, StateCommand(L1, 1, 0, EquipmentStates.Stopped, "CHANGEOVER"));
        await Ok(host, StateCommand(L1, 2, 0, EquipmentStates.Running, null));
        await Ok(host, StateCommand(L1, 3, 0, EquipmentStates.Stopped, "MECH_BREAKDOWN"));
        await Ok(host, StateCommand(L1, 3, 30, EquipmentStates.Running, null));
        await Ok(host, StateCommand(L1, 5, 0, EquipmentStates.Stopped, "MECH_JAM"));
        await Ok(host, StateCommand(L1, 5, 3, EquipmentStates.Running, null));
        (await State(host, L1, 4, 0, EquipmentStates.Stopped, "MECH_JAM")).ReasonCode.ShouldBe(EquipmentReasonCodes.OutOfOrder);
        (await Count(host, L1, 0, 4, 10_000, 9_500, "retry")).Accepted.ShouldBeTrue();   // cùng submission sẽ phát lại lời từ chối cũ
        (await Count(host, L1, 4, 8, 10_000, 9_500)).Accepted.ShouldBeTrue();
        (await Count(host, L1, 4, 8, 1, 1, "again")).ReasonCode.ShouldBe(EquipmentReasonCodes.CountWindowExists);

        // Line 2 không có lệnh 6 giờ đầu (kế hoạch), chạy 2 giờ cuối.
        await Ok(host, StateCommand(L2, 0, 0, EquipmentStates.Stopped, "NO_ORDER"));
        await Ok(host, StateCommand(L2, 6, 0, EquipmentStates.Running, null));
        (await Count(host, L2, 6, 8, 7_000, 6_930)).Accepted.ShouldBeTrue();

        var stream = await new SqlEventStore(new SqlCommandSession(), new SqlEventStoreOptions { ConnectionString = sql.ConnectionString },
            new EventUpcasterChain([])).ReadStreamAsync("NV1", "equipment:" + L1, Ct);
        stream.ShouldNotBeNull();
        stream.Events.Count(e => e.EventType.EndsWith("equipment-downtime-recorded.v1", StringComparison.Ordinal)).ShouldBe(3);

        var report = await host.GetRequiredService<OeeQueries>().CalculateAsync("NV1", [], T0, T0.AddHours(8), Ct);
        var line1 = report.Equipment.Single(l => l.Scope == L1);
        line1.PlannedSeconds.ShouldBe(25_200m);
        line1.UnplannedDowntimeSeconds.ShouldBe(1_800m);
        line1.MicroStopSeconds.ShouldBe(180m);
        line1.Availability!.Value.ShouldBe(23_400m / 25_200m, 0.0000000001m);
        line1.Performance!.Value.ShouldBe(20_000m / 23_400m, 0.0000000001m);
        line1.Oee!.Value.ShouldBe(19_000m / 25_200m, 0.0000000001m);
        var line2 = report.Equipment.Single(l => l.Scope == L2);
        line2.Oee!.Value.ShouldBe(0.9625m, 0.0000000001m);

        // Ground truth: cộng base rồi tính lại = 25.930 s hàng tốt lý tưởng / 32.400 s kế hoạch.
        report.Combined.Oee!.Value.ShouldBe(25_930m / 32_400m, 0.0000000001m);
        var averageOfPercentages = (line1.Oee.Value + line2.Oee.Value) / 2;
        Math.Abs(report.Combined.Oee.Value - averageOfPercentages).ShouldBeGreaterThan(0.05m);

        // Máy đang dừng: lần dừng mở được tính tới "bây giờ" và phân loại theo độ dài tới lúc đó.
        await Ok(host, StateCommand(L2, 8, 30, EquipmentStates.Stopped, "UNASSIGNED"));
        var open = await host.GetRequiredService<OeeQueries>().CalculateAsync("NV1", [L2], T0.AddHours(8), T0.AddHours(10), Ct);
        open.Equipment.Single().UnplannedDowntimeSeconds.ShouldBe(1_800m);   // 08:30 → 09:00 (clock), đã quá 5 phút

        // Site khác không thấy máy NV1.
        (await host.GetRequiredService<OeeQueries>().CalculateAsync("DE1", [], T0, T0.AddHours(8), Ct)).Equipment.ShouldBeEmpty();
        (await host.GetRequiredService<OeeQueries>().StatusAsync("DE1", Ct)).ShouldBeEmpty();
        (await host.GetRequiredService<OeeQueries>().ReasonTreeAsync("DE1", Ct)).ShouldContain(r => r.Code == "CHANGEOVER" && r.IsLeaf);
    }

    private static async Task Ok(ServiceProvider host, DurableCommand command)
    {
        var result = await host.DispatchAsync<DomainCommandResult>(command, Ct);
        result.Accepted.ShouldBeTrue($"{command.CommandType}: {result.ReasonCode} {result.ReasonText}");
    }

    private static ChangeEquipmentStateCommand StateCommand(string path, int hour, int minute, string state, string? reason) =>
        new("NV1", "plc", $"{path}-{hour}-{minute}-{state}-{reason}", T0.AddHours(hour).AddMinutes(minute), path, state, reason);

    private static Task<DomainCommandResult> State(ServiceProvider host, string path, int hour, int minute, string state,
        string? reason) => host.DispatchAsync(StateCommand(path, hour, minute, state, reason), Ct);

    private static Task<DomainCommandResult> Count(ServiceProvider host, string path, int fromHour, int toHour, long total,
        long good, string tag = "") =>
        host.DispatchAsync(new RecordProductionCountCommand("NV1", "plc", $"count-{path}-{fromHour}-{tag}", T0.AddHours(toHour), path,
            Product, T0.AddHours(fromHour), T0.AddHours(toHour), total, good), Ct);
}
