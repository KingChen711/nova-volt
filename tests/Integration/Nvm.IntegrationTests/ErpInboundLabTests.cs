using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nvm.Contracts.Ports;
using Nvm.ErpGateway;
using Nvm.Kernel.Commands;
using Nvm.MasterData.Commands;
using Nvm.ProductionExecution.Hosting;

namespace Nvm.IntegrationTests;

/// <summary>Lab M11: thả 1.000 file B2MML cùng lúc; watcher tuần tự có xử lý nổi không, có cần hàng đợi không.</summary>
[Collection(EventStoreLatencyDefinition.Name)]
public sealed class ErpInboundLabTests(SqlCommandStoreFixture sql, ITestOutputHelper output)
    : IClassFixture<SqlCommandStoreFixture>, IDisposable
{
    private const int Files = 1_000;
    private static readonly DateTimeOffset T0 = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nvm-erp-lab-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task ThousandFilesAtOnce_AreDrainedSequentially_InOneRun()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M11 inbound lab.");
        await TestCommandHost.MigrateAsync(sql.ConnectionString, Ct);
        var clock = new FakeTimeProvider(T0);
        await using var host = TestCommandHost.Build(sql.ConnectionString, clock);
        foreach (var command in new DurableCommand[]
        {
            new DefineCanonicalItemCommand("NV1", "pm", "lab-cam", T0, IdentityKinds.Material, "MAT-0009812", "CAM", "kg"),
            new DefineCanonicalItemCommand("NV1", "pm", "lab-product", T0, IdentityKinds.Product, "NV-P120-NMC", "Pack", null),
        })
        { (await host.DispatchAsync<DomainCommandResult>(command, Ct)).Accepted.ShouldBeTrue(); }
        var options = new ErpGatewayOptions { Enabled = true, SiteId = "NV1", RootDirectory = _root };
        Directory.CreateDirectory(options.Inbound);
        for (var i = 0; i < Files; i++)
        {
            var id = i.ToString("D5", CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(Path.Combine(options.Inbound, $"ps-{id}.xml"),
                ErpGatewayTests.Schedule("PS-LAB-" + id, "WO-LAB-" + id, "NV-P120-NMC", "MAT-0009812", "480", "kg"), Ct);
        }
        var inbound = new ErpInboundProcessor(host.GetRequiredService<IServiceScopeFactory>(), options, clock,
            NullLogger<ErpInboundProcessor>.Instance);
        var watch = Stopwatch.StartNew();
        var run = await inbound.RunOnceAsync(Ct);
        watch.Stop();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"INBOUND files={run.Files} processed={run.Processed} rejected={run.Rejected} seconds={watch.Elapsed.TotalSeconds:F2} files_per_second={run.Files / watch.Elapsed.TotalSeconds:F0}"));
        run.ShouldBe(new InboundRunSummary(Files, Files, 0, 0, Files));
        (await host.GetRequiredService<WorkOrderQueries>().ListAsync("NV1", null, 5_000, Ct)).Count.ShouldBe(Files);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        { Directory.Delete(_root, recursive: true); }
    }
}
