using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Product;
using Testcontainers.MsSql;

// SQL trong harness là SQL của chính test (tên database sinh ngẫu nhiên), không có dữ liệu người dùng.
#pragma warning disable CA2100

namespace Nvm.ProductTests;

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
        var name = "nvp_" + Guid.NewGuid().ToString("N")[..16];
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

/// <summary>
/// Foundation + lớp Product trên một DB mới, gọi qua base app Production. Dữ liệu nền: cây thiết bị nhỏ, hai material
/// (một có serial, một theo lô), một process hai bước, bộ trạng thái WO mặc định.
/// </summary>
internal sealed class ProductHarness
{
    public const string Area = "NV1-B1-L1-MOD";
    public const string Subarea = "NV1-B1-L1-MOD-PRE";
    public const string Machine = "NV1-B1-L1-MOD-OP10-M1";
    public const string Dock = "NV1-B1-DOCK-01";
    public const string Module = "NV-MOD-A";
    public const string Paste = "NV-PASTE-01";
    public const string Process = "ModuleAssembly";

    public static readonly ModelRegistry Registry = ModelRegistry.Build(typeof(ProductionApp).Assembly);

    private ProductHarness(string connectionString)
    {
        Executor = new CommandExecutor(connectionString, Registry, new SolutionConfiguration(), Time, Traces, Events);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 28, 7, 0, 0, TimeSpan.Zero));

    public InMemoryTraceSink Traces { get; } = new();

    public InMemoryEventPublisher Events { get; } = new();

    public CommandExecutor Executor { get; }

    public static async Task<ProductHarness> CreateAsync()
    {
        var connectionString = await SharedSqlServer.NewDatabaseAsync();
        SchemaUpdater.UpdateDatabase(connectionString, Registry);
        var harness = new ProductHarness(connectionString);
        harness.Seed();
        return harness;
    }

    /// <summary>Mỗi lệnh cách nhau một giây, để lịch sử MTU sắp được theo thời gian.</summary>
    public CommandResult Run(object command, CallerIdentity? caller = null)
    {
        Time.Advance(TimeSpan.FromSeconds(1));
        return Executor.Execute(ProductionApp.Name, command, caller);
    }

    /// <summary>Chạy và đòi thành công; trả response để đọc Id, NId.</summary>
    public ProductResponse Ok(object command, CallerIdentity? caller = null)
    {
        var result = Run(command, caller);
        result.Response.ShouldNotBeNull(result.Body.ToJsonString());
        result.Response.Succeeded.ShouldBeTrue(result.Body.ToJsonString());
        return result.As<ProductResponse>();
    }

    /// <summary>Chạy và đòi thất bại với đúng mã lỗi.</summary>
    public void Fails(object command, int errorCode, CallerIdentity? caller = null)
    {
        var result = Run(command, caller);
        result.Response.ShouldNotBeNull(result.Body.ToJsonString());
        result.Response.Error.ShouldNotBeNull(result.Body.ToJsonString());
        result.Response.Error.ErrorCode.ShouldBe(errorCode, result.Body.ToJsonString());
    }

    /// <summary>Đọc như OData của app: bản đã commit, đã lọc xóa mềm.</summary>
    public List<T> Read<T>(Func<IQueryable<T>, IQueryable<T>>? filter = null)
        where T : class
    {
        using var session = Executor.OpenRead();
        var set = session.SetOf<T>();
        return [.. (filter?.Invoke(set) ?? set)];
    }

    public Mtu Mtu(string nid) => Read<Mtu>(q => q.Where(m => m.NId == nid)).Single();

    public static CallerIdentity Caller(params string[] roles) => new("tester", new HashSet<string>(roles, StringComparer.Ordinal));

    private void Seed()
    {
        Ok(new CreateEquipment { NId = "NOVAVOLT", Level = "Enterprise" });
        Ok(new CreateEquipment { NId = "NV1", Level = "Plant", ParentNId = "NOVAVOLT" });
        Ok(new CreateEquipment { NId = Area, Level = "Area", ParentNId = "NV1" });
        Ok(new CreateEquipment { NId = Subarea, Level = "Subarea", ParentNId = Area });
        Ok(new CreateEquipment { NId = Machine, Level = "Machine", ParentNId = Subarea });
        Ok(new CreateEquipment { NId = Dock, Level = "WorkCenter", ParentNId = "NV1" });

        Ok(new CreateMaterial { NId = Module, UnitOfMeasure = "pc", Serialized = true });
        Ok(new CreateMaterial { NId = Paste, UnitOfMeasure = "l" });
        Ok(new CreateProcess { NId = Process, ProductMaterialNId = Module });
        Ok(new AddProcessOperation { ProcessNId = Process, OperationNId = "OP10", Sequence = 10 });
        Ok(new AddProcessOperation { ProcessNId = Process, OperationNId = "OP20", Sequence = 20 });
        Ok(new AllowOperationEquipment { ProcessOperationNId = Process + ":OP10", EquipmentNId = Machine });
        Ok(new AddOperationMaterial { ProcessOperationNId = Process + ":OP10", MaterialNId = Paste, Quantity = 0.25m });

        Ok(new InstallWorkOrderStates());
    }
}
