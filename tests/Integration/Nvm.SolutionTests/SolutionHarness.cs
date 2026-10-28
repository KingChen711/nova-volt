using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Product;
using Nvm.Solution;
using Nvm.Solution.Suites;
using Testcontainers.MsSql;

// SQL trong harness là SQL của chính test (tên database sinh ngẫu nhiên), không có dữ liệu người dùng.
#pragma warning disable CA2100

namespace Nvm.SolutionTests;

/// <summary>Một SQL Server cho cả lượt test; mỗi harness một database riêng, đã chạy Update Database.</summary>
internal static class SharedSqlServer
{
    private static readonly Lazy<Task<MsSqlContainer>> Container = new(async () =>
    {
        // Cùng image với docker-compose.yml.
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04").Build();
        await container.StartAsync(CancellationToken.None);
        return container;
    });

    public static async Task<string> NewDatabaseAsync()
    {
        var container = await Container.Value;
        var name = "nvs_" + Guid.NewGuid().ToString("N")[..16];
        var master = container.GetConnectionString();
        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new SqlCommand($"CREATE DATABASE [{name}];", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString;
    }
}

/// <summary>Plant NV1 trên một DB mới, với manifest và suite của repo. Giờ bắt đầu: thứ Hai 26/10/2026 06:00 UTC.</summary>
internal sealed class SolutionHarness
{
    public static readonly ModelRegistry Registry = SolutionManifest.BuildRegistry();

    private SolutionHarness(string connectionString, DateTimeOffset start)
    {
        ConnectionString = connectionString;
        Time = new FakeTimeProvider(start);
        Executor = new CommandExecutor(connectionString, Registry, SolutionManifest.Configure(), Time, Traces, Events);
        Rules = new SignalRuleEngine(Executor, connectionString);
        HistoryFile = Path.Combine(Path.GetTempPath(), "nvs-" + Guid.NewGuid().ToString("N") + ".txt");
    }

    public string ConnectionString { get; }

    public FakeTimeProvider Time { get; }

    public InMemoryTraceSink Traces { get; } = new();

    public InMemoryEventPublisher Events { get; } = new();

    public CommandExecutor Executor { get; }

    public SignalRuleEngine Rules { get; }

    public string HistoryFile { get; }

    public static string RepoRoot([CallerFilePath] string path = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NovaVolt.Mes.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    public static string SuitesRoot => Path.Combine(RepoRoot(), "suites");

    public static string Fixtures([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures");

    public static async Task<SolutionHarness> CreateAsync(DateTimeOffset? start = null)
    {
        var connectionString = await SharedSqlServer.NewDatabaseAsync();
        SchemaUpdater.UpdateDatabase(connectionString, Registry);
        return new SolutionHarness(connectionString, start ?? new DateTimeOffset(2026, 10, 26, 6, 0, 0, TimeSpan.Zero));
    }

    public static async Task<SolutionHarness> WithMasterDataAsync(DateTimeOffset? start = null)
    {
        var harness = await CreateAsync(start);
        harness.Deploy().Where(s => s.Status == SuiteStatus.Failed).ShouldBeEmpty();
        return harness;
    }

    /// <summary>Bước nạp master data của deploy, chạy dưới người gọi hệ thống như script deploy.</summary>
    public IReadOnlyList<DeployedSuite> Deploy(string? root = null, HistoryPolicy policy = HistoryPolicy.OnSuccess) =>
        MasterDataDeployment.Run(root ?? SuitesRoot, Target(), HistoryFile, policy);

    public InProcessTarget Target() => new(Executor, ConnectionString, CallerIdentity.System);

    public CommandResult Run(object command, CallerIdentity? caller = null, string app = ProductionApp.Name) => Executor.Execute(app, command, caller);

    public List<T> Read<T>(Func<IQueryable<T>, IQueryable<T>>? filter = null)
        where T : class
    {
        using var session = Executor.OpenRead();
        var set = session.SetOf<T>();
        return [.. (filter?.Invoke(set) ?? set)];
    }

    /// <summary>Cho giờ chạy tới <paramref name="until"/> từng giờ một, chạy timer tới hạn ở mỗi bước.</summary>
    public void RunClockUntil(DateTimeOffset until)
    {
        while (Time.GetUtcNow() < until)
        {
            var next = Time.GetUtcNow().AddHours(1);
            Time.SetUtcNow(next < until ? next : until);
            Rules.RunDueTimers();
        }
    }

    public static CallerIdentity Caller(string user, params string[] roles) => new(user, new HashSet<string>(roles, StringComparer.Ordinal));
}
