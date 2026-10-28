using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.FoundationTests.LabModel;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;

// SQL trong harness là SQL của chính test (tên bảng từ registry, NId cố định), không có dữ liệu người dùng.
#pragma warning disable CA2100

namespace Nvm.FoundationTests;

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
        var name = "nvm_" + Guid.NewGuid().ToString("N")[..16];
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

/// <summary>Một RabbitMQ cho cả lượt test. Test bus dùng plant riêng, nên exchange và queue không lẫn giữa các test.</summary>
internal static class SharedRabbitMq
{
    private static readonly Lazy<Task<RabbitMqContainer>> Container = new(async () =>
    {
        // Cùng image với docker-compose.yml.
        var container = new RabbitMqBuilder("rabbitmq:4.3.5-management").Build();
        await container.StartAsync(CancellationToken.None);
        return container;
    });

    public static async Task<string> UriAsync() => (await Container.Value).GetConnectionString();
}

/// <summary>Foundation đã dựng trên một DB mới, với model thử nghiệm và hai app: LabApp bật pre-check, OtherApp thì không.</summary>
internal sealed class FoundationHarness
{
    public static readonly ModelRegistry Registry = ModelRegistry.Build(LabTypes);

    /// <summary>Chỉ namespace LabModel: assembly test còn chứa model cố ý sai cho test lỗi khai báo.</summary>
    public static IEnumerable<Type> LabTypes =>
        typeof(LabOrder).Assembly.GetTypes().Where(t => t.Namespace == typeof(LabOrder).Namespace);

    private FoundationHarness(string connectionString, SolutionConfiguration solution, Func<FoundationHarness, IEventPublisher>? publisher)
    {
        ConnectionString = connectionString;
        Solution = solution;
        Executor = new CommandExecutor(connectionString, Registry, solution, Time, Traces, publisher?.Invoke(this) ?? Events);
        Rules = new SignalRuleEngine(Executor, connectionString);
    }

    public string ConnectionString { get; }

    public SolutionConfiguration Solution { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 27, 6, 0, 0, TimeSpan.Zero));

    public InMemoryTraceSink Traces { get; } = new();

    public InMemoryEventPublisher Events { get; } = new();

    public CommandExecutor Executor { get; }

    public SignalRuleEngine Rules { get; }

    /// <summary>Harness có publisher thật (RabbitMQ). <paramref name="publisher"/> null là publisher trong bộ nhớ.</summary>
    public static async Task<FoundationHarness> CreateAsync(
        Action<SolutionConfiguration>? configure = null,
        Func<FoundationHarness, IEventPublisher>? publisher = null)
    {
        var connectionString = await SharedSqlServer.NewDatabaseAsync();
        SchemaUpdater.UpdateDatabase(connectionString, Registry);

        // POM (app nào công bố gì) nằm trong model: LabAppDefinition, OtherAppDefinition. Ở đây chỉ còn command settings.
        var solution = new SolutionConfiguration();
        var lab = new AppConfiguration(LabApp.Name);
        lab.EnabledPreChecks.Add(nameof(RejectNegativeQuantity));
        solution.Apps.Add(lab);
        configure?.Invoke(solution);
        return new FoundationHarness(connectionString, solution, publisher);
    }

    public CommandResult Run(object command, string app = LabApp.Name, CallerIdentity? caller = null) => Executor.Execute(app, command, caller);

    /// <summary>SQL trực tiếp, như sổ tay SQL chỉ đọc: không qua bộ lọc <c>IsDeleted</c> của platform.</summary>
    public T Scalar<T>(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        return (T)command.ExecuteScalar()!;
    }

    public void Execute(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    public string TableOf<T>() => Executor.Registry.TableOf(typeof(T));
}
