using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nvm.CommandStore;
using Nvm.EventStore;
using Nvm.Material.Commands;
using Nvm.ProductionExecution.Commands;
using Nvm.Quality.Commands;
using Nvm.Quality.Hosting;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;

namespace Nvm.IntegrationTests;

/// <summary>
/// Lab phá hoại M9 #2 (scope §6.2): gộp quality state và inventory/location state làm một enum, rồi "chuyển kho" một cell
/// đang bị hold. Mô hình gộp làm cell lọt cổng; hệ thống thật (hai state, hai owner) giữ nguyên Held sau khi cell vào rack.
/// </summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class QualityInventoryStateSeparationTests(SqlCommandStoreFixture sql, ITestOutputHelper output)
    : IClassFixture<SqlCommandStoreFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MovingAHeldCellIntoTheAgingRack_DoesNotReleaseIt_WhileAMergedEnumWould()
    {
        const string cell = "NV1CL16244A03001";
        const string rack = "A-14";

        // Mô hình sai, cố ý: một enum cho cả chất lượng lẫn vị trí. Chuyển kho ghi đè Held.
        var merged = MergedUnitState.InProduction;
        merged = MergedModel.PlaceHold(merged);
        merged = MergedModel.PutAway(merged);
        var mergedLeaks = MergedModel.MayProceed(merged);
        output.WriteLine($"STATE_LAB merged_state={merged} merged_may_proceed={mergedLeaks}");
        mergedLeaks.ShouldBeTrue("mô hình gộp phải tái hiện lỗi: hold bị ghi đè bởi chuyển kho");

        // Hệ thống thật.
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        await TraceabilityFixtureSeed.PrepareAsync(sql.ConnectionString, Ct);
        var clock = new FakeTimeProvider(T0);
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock);
        (await host.DispatchAsync(new SerializeUnitCommand("NV1", "op", "birth-" + cell, cell, clock.GetUtcNow(),
            TraceabilityFixtureSeed.ProductCode, "WO-M9", TraceabilityFixtureSeed.RoutingVersion), Ct)).Accepted.ShouldBeTrue();
        (await host.DispatchAsync(new StartFormationCommand("NV1", "op", "form-" + cell, clock.GetUtcNow(), cell,
            "TRAY-0090", 1, "NOVAVOLT/NV1/FORMATION/F1/FORM-01"), Ct)).Accepted.ShouldBeTrue();
        clock.Advance(TimeSpan.FromHours(30));
        (await host.DispatchAsync(new CompleteFormationCommand("NV1", "op", "done-" + cell, clock.GetUtcNow(), cell,
            51.2m, null), Ct)).Accepted.ShouldBeTrue();

        var holdId = (await host.DispatchAsync(new PlaceHoldCommand("NV1", "qa.eng", "hold-" + cell, clock.GetUtcNow(),
            "Unit", cell, null, null, "CONTAMINATION", null), Ct)).ReasonText!;
        (await new HoldCascadeWorker(host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<SqlCommandStoreOptions>(), new HoldCascadeOptions(TimeSpan.Zero), clock,
            NullLogger<HoldCascadeWorker>.Instance).RunOnceAsync(Ct)).ShouldBe(1);
        (await QualityStateAsync(cell)).ShouldBe("Held");

        // "Chuyển kho": cell vào rack aging. Được phép — hàng đang giữ vẫn phải có chỗ nằm (scope §6.2).
        (await host.DispatchAsync(new StartAgingCommand("NV1", "op", "age-" + cell, clock.GetUtcNow(), cell, 4150m,
            rack, 2, 5), Ct)).Accepted.ShouldBeTrue();
        (await sql.ScalarAsync("""
            SELECT State + ':' + RackId FROM execution.FormationAging WHERE SiteId = 'NV1' AND SerialNumber = @serial;
            """, command => command.Parameters.AddWithValue("@serial", cell), Ct)).ShouldBe("Aging:" + rack);

        var quality = await QualityStateAsync(cell);
        var consume = await host.DispatchAsync(new ConsumeMaterialCommand("NV1", "op", "consume-" + cell, clock.GetUtcNow(),
            cell, "ELY-STATE-LAB", "Lot", "ELECTROLYTE", 1m, "g", null, null, "RUN"), Ct);
        output.WriteLine($"STATE_LAB real_quality={quality} real_consume={consume.ReasonCode} hold={holdId}");
        quality.ShouldBe("Held");
        consume.ReasonCode.ShouldBe("QUALITY_HOLD");
    }

    private async Task<string> QualityStateAsync(string serial)
    {
        string? state = null;
        await sql.SubmitAsync(SqlCommandStoreFixture.NewCommand("NV1", "qa.eng", Guid.NewGuid().ToString("N"), "read quality"),
            async session =>
            {
                var store = new SqlEventStore(session, new SqlEventStoreOptions { ConnectionString = sql.ConnectionString },
                    new EventUpcasterChain([]));
                state = (await new SqlUnitQualityFacet(session, store, TimeProvider.System)
                    .ReadForCommandAsync("NV1", serial, Ct)).QualityState;
                return new CollectionOutcome(true, "READ");
            }, cancellationToken: Ct);
        return state!;
    }

    private enum MergedUnitState { InProduction, Held, InWarehouse, Shipped }

    /// <summary>Ví dụ tự dựng của cách làm sai; không phải code sản phẩm.</summary>
    private static class MergedModel
    {
        public static MergedUnitState PlaceHold(MergedUnitState _) => MergedUnitState.Held;

        // Chuyển kho chỉ biết một cột trạng thái, nên ghi vị trí mới lên đúng cột đang nói "Held".
        public static MergedUnitState PutAway(MergedUnitState _) => MergedUnitState.InWarehouse;

        public static bool MayProceed(MergedUnitState state) => state != MergedUnitState.Held;
    }
}
