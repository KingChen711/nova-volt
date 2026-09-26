using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nvm.EventStore;
using Nvm.Kernel.Commands;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Hosting;
using Nvm.Quality.Hosting;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;

namespace Nvm.IntegrationTests;

/// <summary>M7: saga formation + aging với timeout bền và đồng hồ ảo (T5).</summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class FormationAgingProcessTests(SqlCommandStoreFixture sql, ITestOutputHelper output)
    : IClassFixture<SqlCommandStoreFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwelveVirtualDays_CompleteTheSagaInUnderTwoSeconds_AndSurviveARestart()
    {
        await MigrateAsync();
        var clock = new FakeTimeProvider(T0);
        const string good = "NV1CL16244A01001";
        const string drifting = "NV1CL16244A01002";
        const string stuck = "NV1CL16244A01003";
        await using (var first = Build(clock))
        {
            foreach (var serial in new[] { good, drifting, stuck })
            {
                (await Dispatch(first, new SerializeUnitCommand("NV1", "op", "birth-" + serial, serial, clock.GetUtcNow(),
                    TraceabilityFixtureSeed.ProductCode, "WO-M7", TraceabilityFixtureSeed.RoutingVersion))).Accepted.ShouldBeTrue();
            }
        }

        var watch = Stopwatch.StartNew();
        await using (var host = Build(clock))
        {
            foreach (var (serial, channel) in new[] { (good, 1), (drifting, 2), (stuck, 3) })
            {
                (await Dispatch(host, new StartFormationCommand("NV1", "op", "form-" + serial, clock.GetUtcNow(), serial,
                    "TRAY-0042", channel, "NOVAVOLT/NV1/FORMATION/F1/FORM-01"))).Accepted.ShouldBeTrue();
            }
            clock.Advance(TimeSpan.FromHours(30));
            foreach (var serial in new[] { good, drifting })
            {
                (await Dispatch(host, new CompleteFormationCommand("NV1", "op", "done-" + serial, clock.GetUtcNow(), serial,
                    51.2m, null))).Accepted.ShouldBeTrue();
                (await Dispatch(host, new StartAgingCommand("NV1", "op", "age-" + serial, clock.GetUtcNow(), serial,
                    4150m, "A-12", 3, serial == good ? 7 : 8))).Accepted.ShouldBeTrue();
            }
            (await Dispatch(host, new RecordOcv2Command("NV1", "op", "early", clock.GetUtcNow(), good, 4149m)))
                .ReasonCode.ShouldBe("AGING_NOT_ELAPSED");
            clock.Advance(TimeSpan.FromHours(7));   // quá 36 giờ: cell thứ ba chưa xong formation
            (await Worker(host).RunOnceAsync(cancellationToken: Ct)).ShouldBe(1);
        }
        (await StateAsync(stuck)).ShouldBe("Faulted");
        (await RackAsync("A-12", 3)).ShouldBe([good, drifting]);
        await using (var reader = Build(clock))
        {
            var queries = reader.GetRequiredService<AgingWarehouseQueries>();
            (await queries.AtRackLevelAsync("NV1", "A-12", 3, Ct)).Select(cell => cell.SerialNumber).ShouldBe([good, drifting]);
            (await queries.AtRackLevelAsync("DE1", "A-12", 3, Ct)).ShouldBeEmpty();
            (await queries.AtRackLevelAsync("NV1", "A-12", 2, Ct)).ShouldBeEmpty();
        }

        // Restart giữa lúc chờ 10 ngày: host mới đọc hạn từ DB, không cần RAM của host cũ.
        clock.Advance(TimeSpan.FromDays(10.5));
        await using (var restarted = Build(clock))
        {
            (await Worker(restarted).RunOnceAsync(cancellationToken: Ct)).ShouldBe(2);
            (await Worker(restarted).RunOnceAsync(cancellationToken: Ct)).ShouldBe(0);
            (await Dispatch(restarted, new RecordOcv2Command("NV1", "op", "ocv2-good", clock.GetUtcNow(), good, 4146m)))
                .Accepted.ShouldBeTrue();
            (await Dispatch(restarted, new RecordOcv2Command("NV1", "op", "ocv2-drift", clock.GetUtcNow(), drifting, 4131m)))
                .Accepted.ShouldBeTrue();
        }
        watch.Stop();
        output.WriteLine($"FORMATION_SAGA virtual_days={(clock.GetUtcNow() - T0).TotalDays:F2} wall_ms={watch.Elapsed.TotalMilliseconds:F0}");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "T5: 12 virtual days must run in under two seconds");

        (await StateAsync(good)).ShouldBe("Completed");
        (await StateAsync(drifting)).ShouldBe("Quarantined");
        (await sql.ScalarAsync("""
            SELECT QualityState + ':' + ReasonCode FROM quality.UnitQuality
            WHERE SiteId = 'NV1' AND SerialNumber = 'NV1CL16244A01002';
            """, _ => { }, Ct)).ShouldBe("Held:OCV_DRIFT");
        (await sql.ScalarAsync("""
            SELECT Status + ':' + ReasonCode FROM quality.NonConformance
            WHERE SiteId = 'NV1' AND SerialNumber = 'NV1CL16244A01002';
            """, _ => { }, Ct)).ShouldBe("Open:OCV_DRIFT");
        (await sql.ScalarAsync("""
            SELECT CONVERT(varchar(10), COUNT(*)) FROM quality.UnitQuality
            WHERE SiteId = 'NV1' AND SerialNumber = 'NV1CL16244A01001';
            """, _ => { }, Ct)).ShouldBe("0");
        (await RackAsync("A-12", 3)).ShouldBeEmpty();
    }

    [Fact]
    public async Task LostTimeout_IsDetectedAndRescheduled_AndLostStateIsReported()
    {
        await MigrateAsync();
        var clock = new FakeTimeProvider(T0);
        const string serial = "NV1CL16244A02001";
        const string ghost = "NV1CL16244A02002";
        await using (var host = Build(clock))
        {
            foreach (var cell in new[] { serial, ghost })
            {
                await Dispatch(host, new SerializeUnitCommand("NV1", "op", "birth-" + cell, cell, clock.GetUtcNow(),
                    TraceabilityFixtureSeed.ProductCode, "WO-M7", TraceabilityFixtureSeed.RoutingVersion));
                (await Dispatch(host, new StartFormationCommand("NV1", "op", "form-" + cell, clock.GetUtcNow(), cell,
                    "TRAY-0043", 1, "NOVAVOLT/NV1/FORMATION/F1/FORM-01"))).Accepted.ShouldBeTrue();
            }
        }
        // Lab phá hoại: xoá lịch timeout của một cell và toàn bộ trạng thái của cell kia.
        await sql.ExecuteAsync("""
            DELETE FROM execution.ProcessTimeouts WHERE SerialNumber IN ('NV1CL16244A02001', 'NV1CL16244A02002');
            DELETE FROM execution.FormationAging WHERE SerialNumber = 'NV1CL16244A02002';
            """, _ => { }, Ct);
        clock.Advance(TimeSpan.FromHours(40));
        await using (var host = Build(clock))
        { (await Worker(host).RunOnceAsync(cancellationToken: Ct)).ShouldBe(0); }   // không còn gì đánh thức cell

        var report = await FormationReconciliation.RunAsync(sql.ConnectionString, "NV1", Ct);
        report.MissingTimeouts.ShouldBe(1);
        report.StreamsWithoutState.ShouldContain("formation:" + ghost);
        (await FormationReconciliation.RunAsync(sql.ConnectionString, "NV1", Ct)).MissingTimeouts.ShouldBe(0);
        await using (var host = Build(clock))
        { (await Worker(host).RunOnceAsync(cancellationToken: Ct)).ShouldBe(1); }
        (await StateAsync(serial)).ShouldBe("Faulted");
    }

    private async Task MigrateAsync()
    {
        await EventSchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await ProductionExecutionSchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await QualitySchemaMigrator.UpgradeAsync(sql.ConnectionString, Ct);
        await TraceabilityFixtureSeed.PrepareAsync(sql.ConnectionString, Ct);
    }

    private ServiceProvider Build(TimeProvider clock) => TestCommandHost.Build(sql.ConnectionString, clock);

    private static FormationTimeoutWorker Worker(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<Nvm.ProductionExecution.Ports.IDueTimeoutSource>(),
            provider.GetRequiredService<TimeProvider>(), NullLogger<FormationTimeoutWorker>.Instance);

    private static async Task<DomainCommandResultView> Dispatch<TResult>(ServiceProvider provider, ICommand<TResult> command)
    {
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>().DispatchAsync<TResult>(command, Ct);
        return result switch
        {
            DomainCommandResult domain => new(domain.Accepted, domain.ReasonCode),
            UnitCommandResult unit => new(unit.Accepted, unit.ReasonCode),
            _ => throw new InvalidOperationException("Unexpected result type.")
        };
    }

    private Task<string?> StateAsync(string serial) => sql.ScalarAsync(
        "SELECT State FROM execution.FormationAging WHERE SiteId = 'NV1' AND SerialNumber = @serial;",
        command => command.Parameters.AddWithValue("@serial", serial), Ct);

    private async Task<string[]> RackAsync(string rack, int level)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(Ct);
        using var command = new Microsoft.Data.SqlClient.SqlCommand("""
            SELECT SerialNumber FROM execution.FormationAging
            WHERE SiteId = 'NV1' AND RackId = @rack AND Level = @level AND State = 'Aging' ORDER BY AgingChannel;
            """, connection);
        command.Parameters.AddWithValue("@rack", rack);
        command.Parameters.AddWithValue("@level", level);
        var serials = new List<string>();
        using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        { serials.Add(reader.GetString(0)); }
        return [.. serials];
    }

    private sealed record DomainCommandResultView(bool Accepted, string ReasonCode);

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Nvm.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
