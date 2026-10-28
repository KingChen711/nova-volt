using Npgsql;
using Nvm.Foundation;
using Nvm.Project;
using Nvm.Shopfloor;
using Nvm.Shopfloor.LineDb;
using Testcontainers.PostgreSql;

namespace Nvm.SolutionTests;

/// <summary>Một Postgres cho cả lượt test; mỗi stack một database LineDB riêng, đã cài schema.</summary>
internal static class SharedLineDb
{
    public const string MomPassword = "mom-test-password";

    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
    {
        // Cùng image với docker-compose.yml.
        var container = new PostgreSqlBuilder("postgres:17.6").Build();
        await container.StartAsync(CancellationToken.None);
        return container;
    });

    /// <summary>Chuỗi kết nối của owner (đóng vai máy và phía xưởng) và của role MOM (chỉ gọi được hàm trung gian).</summary>
    public static async Task<(string Owner, string Mom)> NewDatabaseAsync(LineStateStorage storage, string environment)
    {
        var container = await Container.Value;
        var name = "ldb_" + Guid.NewGuid().ToString("N")[..16];
        await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // tên database sinh trong test
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name};", connection);
#pragma warning restore CA2100
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var owner = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString;

        // Role nvm_mom là của cả cluster: hai lần cài song song cùng tạo hoặc đổi nó thì một bên lỗi.
        await InstallGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            LineDbInstaller.Install(owner, new LineDbOptions { Storage = storage, Environment = environment, MomPassword = MomPassword });
        }
        finally
        {
            InstallGate.Release();
        }

        var mom = new NpgsqlConnectionStringBuilder(owner) { Username = "nvm_mom", Password = MomPassword }.ConnectionString;
        return (owner, mom);
    }
}

/// <summary>MOM NV1 (master data đã nạp) nối với một LineDB mới. Giờ bắt đầu: thứ Hai 26/10/2026 06:00 UTC.</summary>
internal sealed class LineStack
{
    /// <summary>Mã bước phía xưởng theo thứ tự của chuỗi.</summary>
    public static readonly string[] Operations = ["el010", "ca010", "fo010", "mo010"];

    private LineStack(SolutionHarness mom, string owner, string momDb)
    {
        Mom = mom;
        Owner = owner;
        MomDb = momDb;
    }

    public SolutionHarness Mom { get; }

    /// <summary>Owner của LineDB: máy ghi bảng, lịch phía xưởng, người sửa dữ liệu tay.</summary>
    public string Owner { get; }

    public string MomDb { get; }

    public static async Task<LineStack> CreateAsync(string transfer = "poll", LineStateStorage storage = LineStateStorage.Table,
        string environment = "qa", DateTimeOffset? start = null)
    {
        var mom = await SolutionHarness.WithMasterDataAsync(start);
        var (owner, momDb) = await SharedLineDb.NewDatabaseAsync(storage, environment);
        mom.Executor.Solution.Endpoints[NvEndpoints.LineDb] = momDb;
        var stack = new LineStack(mom, owner, momDb);
        stack.SetKey(NvKeys.LineTransfer, transfer);
        return stack;
    }

    public void SetKey(string nid, string value) =>
        Mom.Run(new SetConfigurationKey { NId = nid, Val = value }, app: "Foundation").StatusCode.ShouldBe(200);

    /// <summary>Cho giờ chạy tới đầu ca S1 hôm nay (07:00 UTC): timer đầu ca mở WO của 16 khu.</summary>
    public void OpenDayShift() => Mom.RunClockUntil(new DateTimeOffset(2026, 10, 26, 7, 1, 0, TimeSpan.Zero));

    public LineScenarioExpectation Simulate(LineScenario scenario) => new LineSimulator(Owner).Run(scenario);

    /// <summary>Đường poll, gọi tay thay cho signal rule: mỗi mã bước theo thứ tự chuỗi, đọc tới khi không còn dòng tới lượt.</summary>
    public int Poll(params string[] operations)
    {
        var batches = 0;
        foreach (var operation in operations.Length > 0 ? operations : Operations)
        {
            while (true)
            {
                Mom.Run(new IngestLineMessages { OperationCode = operation }).StatusCode.ShouldBe(200);
                if (Mom.DeliverEvents() == 0)
                {
                    break;
                }

                batches++;
            }
        }

        return batches;
    }

    /// <summary>Một nhịp của lịch phía xưởng trên đường relay. Trả số dòng đã notify.</summary>
    public int SendPending(int rowsPerMessage = 5) => LineRelay.SendPending(Owner, rowsPerMessage, Mom.Time.GetUtcNow());

    /// <summary>Bên nghe của đường relay, nối thẳng vào MOM như host làm.</summary>
    public LineNotifyListener Listener() => new(MomDb, payload =>
    {
        Mom.Executor.RaiseExternalEvent("FB_OP_Nv." + nameof(LineMessageArrived),
            System.Text.Json.JsonSerializer.Serialize(new LineMessageArrived("relay", payload)));
        return Task.CompletedTask;
    });

    /// <summary>Số dòng tin theo (trạng thái, mã lỗi).</summary>
    public Dictionary<(string Status, int Code), int> Statuses()
    {
        var result = new Dictionary<(string, int), int>();
        using var connection = new NpgsqlConnection(Owner);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT status, error_code, count(*)::int FROM relay.message_status GROUP BY 1, 2;", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[(reader.GetString(0), reader.GetInt32(1))] = reader.GetInt32(2);
        }

        return result;
    }

    public (string Status, int Code, int Attempts) Row(string serial, string operation)
    {
        using var connection = new NpgsqlConnection(Owner);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT s.status, s.error_code, s.attempt_count
            FROM relay.line_message m JOIN relay.message_status s ON s.sequence_id = m.sequence_id
            WHERE m.serial = @serial AND m.operation_code = @operation;
            """, connection);
        command.Parameters.AddWithValue("serial", serial);
        command.Parameters.AddWithValue("operation", operation);
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue($"no line row for {serial} at {operation}");
        return (reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <summary>Sửa tay của DBA (mẫu gửi lại của team): đặt các dòng thỏa điều kiện về NEW, số lần gửi về 0.</summary>
    public int Resend(string condition)
    {
        using var connection = new NpgsqlConnection(Owner);
        connection.Open();
#pragma warning disable CA2100 // điều kiện là SQL của chính test
        using var command = new NpgsqlCommand($"""
            UPDATE relay.message_status SET status = 'NEW', attempt_count = 0, error_code = 0
            WHERE sequence_id IN (SELECT s.sequence_id FROM relay.message_status s WHERE {condition});
            """, connection);
#pragma warning restore CA2100
        return command.ExecuteNonQuery();
    }

    public T Scalar<T>(string sql)
    {
        using var connection = new NpgsqlConnection(Owner);
        connection.Open();
#pragma warning disable CA2100
        using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)command.ExecuteScalar()!;
    }
}
