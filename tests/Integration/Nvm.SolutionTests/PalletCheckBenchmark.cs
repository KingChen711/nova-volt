using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Nvm.Product;
using Nvm.Solution;

namespace Nvm.SolutionTests;

/// <summary>
/// DoD R7: kiểm hàng lỗi đệ quy của một pallet (procedure của phần thành phẩm) trên dữ liệu 30 ngày mô phỏng, p95 &lt; 1 s. Dữ liệu
/// sinh thẳng bằng SQL theo hình dạng của chuỗi tin (không qua command, cho nhanh): 30 ngày × 2 ca × 4 line.
/// </summary>
/// <remarks>Số đo ghi vào docs/benchmarks.md kèm ngày và commit.</remarks>
public sealed class PalletCheckBenchmark
{
    private const int Measured = 50;

    /// <summary>Chạy riêng sau lượt test chung (Makefile, CI), như benchmark của R1.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task Recursive_held_item_check_on_thirty_days_of_line_data()
    {
        var h = await SolutionHarness.CreateAsync();
        ReadingViews.Install(h.ConnectionString, SolutionHarness.Registry);
        var load = Stopwatch.StartNew();
        // Họ A: line 1–2, pallet 8 tote, tote một kit, kit hai module, module 12 cell. Họ B: line 3–4, pallet 6 tote, tote một
        // module, module 60 cell. Mỗi ca mỗi line: A 8 pallet, B 6 pallet. 120 ca-line mỗi họ.
        var a = Generate(h.ConnectionString, "A", batches: 120, palletsPerBatch: 8, totesPerPallet: 8, modulesPerTote: 2, cellsPerModule: 12);
        var b = Generate(h.ConnectionString, "B", batches: 120, palletsPerBatch: 6, totesPerPallet: 6, modulesPerTote: 1, cellsPerModule: 60);
        load.Stop();
        var mtus = Scalar(h.ConnectionString, $"SELECT COUNT_BIG(*) FROM dbo.[{Table<Mtu>()}]");
        var rows = Scalar(h.ConnectionString, $"SELECT COUNT_BIG(*) FROM dbo.[{Table<MaterialConsumption>()}]");

        var pallets = a.Concat(b).OrderBy(_ => Random.Shared.Next()).Take(Measured).ToList();
        var samples = new double[Measured];
        var held = 0;
        for (var i = 0; i < Measured; i++)
        {
            var started = Stopwatch.GetTimestamp();
            held += Held(h.ConnectionString, pallets[i]);
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(samples);
        double P(double q) => samples[(int)Math.Ceiling(q * Measured) - 1];
        var line = string.Create(CultureInfo.InvariantCulture,
            $"pallets={a.Count + b.Count} mtus={mtus} consumptions={rows} load={load.Elapsed.TotalSeconds:F0}s calls={Measured} held={held} "
            + $"p50={P(0.50):F0}ms p95={P(0.95):F0}ms max={samples[^1]:F0}ms");
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        await File.AppendAllTextAsync(Path.Combine(AppContext.BaseDirectory, "pallet-check.txt"), line + Environment.NewLine,
            TestContext.Current.CancellationToken);

        held.ShouldBeGreaterThan(0);
        P(0.95).ShouldBeLessThan(1000);
    }

    private static string Table<T>() => SolutionHarness.Registry.Entities.Single(e => e.Type == typeof(T)).TableName;

    /// <summary>Sinh pallet, tote, hàng, cây genealogy của một họ; khoảng 0,1 % cell bị giữ kèm NC mở. Trả mã các pallet.</summary>
    private static List<string> Generate(string connection, string family, int batches, int palletsPerBatch, int totesPerPallet,
        int modulesPerTote, int cellsPerModule)
    {
        var pallets = batches * palletsPerBatch;
        var totes = pallets * totesPerPallet;
        var modules = totes * modulesPerTote;
        var cells = modules * cellsPerModule;
        var f = family;
        var (mtu, mtua, use, nc, item) = (Table<Mtu>(), Table<Mtua>(), Table<MaterialConsumption>(), Table<NonConformance>(), Table<NonConformanceItem>());
        // Hàng trong tote: kit (họ A, ghép modulesPerTote module) hoặc chính module (họ B).
        var kit = modulesPerTote > 1;
        var items = kit
            ? $"""
                INSERT INTO dbo.[{mtu}] (Id, NId, CreatedOn, LastUpdatedOn, MaterialNId, Code, Kind, Quantity, Status, ParentMtuaNId, InContainment)
                SELECT NEWID(), CONCAT('B{f}-K', i), @now, @now, 'NV-FG-KIT-A', CONCAT('K', i), 'Serial', 1, 'Available', CONCAT('B{f}-T', i), 0
                FROM #n WHERE i < {totes};
                INSERT INTO dbo.[{mtu}] (Id, NId, CreatedOn, LastUpdatedOn, MaterialNId, Code, Kind, Quantity, Status, EquipmentNId, InContainment)
                SELECT NEWID(), CONCAT('B{f}-M', i), @now, @now, 'NV-FG-MOD-A', CONCAT('M', i), 'Serial', 0, 'Available', 'n/a', 0
                FROM #n WHERE i < {modules};
                INSERT INTO dbo.[{use}] (Id, NId, CreatedOn, LastUpdatedOn, WorkOrderNId, ProductMtuNId, ComponentMtuNId, Quantity)
                SELECT NEWID(), CONCAT('B{f}-UK', i), @now, @now, 'WO-SIM', CONCAT('B{f}-K', i / {modulesPerTote}), CONCAT('B{f}-M', i), 1
                FROM #n WHERE i < {modules};

                """
            : $"""
                INSERT INTO dbo.[{mtu}] (Id, NId, CreatedOn, LastUpdatedOn, MaterialNId, Code, Kind, Quantity, Status, ParentMtuaNId, InContainment)
                SELECT NEWID(), CONCAT('B{f}-M', i), @now, @now, 'NV-FG-MOD-B60', CONCAT('M', i), 'Serial', 1, 'Available', CONCAT('B{f}-T', i), 0
                FROM #n WHERE i < {modules};

                """;
        Exec(connection, $"""
            DECLARE @now datetimeoffset(7) = SYSDATETIMEOFFSET();
            WITH n AS (SELECT TOP ({cells}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
                       FROM sys.all_columns x CROSS JOIN sys.all_columns y CROSS JOIN sys.all_columns z)
            SELECT i INTO #n FROM n;

            INSERT INTO dbo.[{mtua}] (Id, NId, CreatedOn, LastUpdatedOn, ContainerType, EquipmentNId, StateMachine, Status, Quantity, MaterialNId)
            SELECT NEWID(), CONCAT('B{f}-P', i), @now, @now, 'Pallet', 'NV1-CMN-FGW-A01', 'PalletStateMachine', 'Full', {totesPerPallet}, 'NV-FG-{f}'
            FROM #n WHERE i < {pallets};
            INSERT INTO dbo.[{mtua}] (Id, NId, CreatedOn, LastUpdatedOn, ContainerType, ParentMtuaNId, StateMachine, Status, Quantity, MaterialNId)
            SELECT NEWID(), CONCAT('B{f}-T', i), @now, @now, 'Tote', CONCAT('B{f}-P', i / {totesPerPallet}), 'ToteStateMachine', 'New', 1, 'NV-FG-{f}'
            FROM #n WHERE i < {totes};

            -- Hàng trong tote, module, cell đã formation, cell, cuộn (1 cuộn mỗi 100 cell), lô màng ngăn và vỏ (1 mỗi ca-line).
            {items}
            INSERT INTO dbo.[{mtu}] (Id, NId, CreatedOn, LastUpdatedOn, MaterialNId, Code, Kind, Quantity, Status, EquipmentNId, InContainment)
            SELECT NEWID(), CONCAT('B{f}-F', i), @now, @now, 'NV-WIP-FCELL-{f}', CONCAT('C', i), 'Serial', 0,
                   CASE WHEN i % 997 = 13 THEN 'OnHold' ELSE 'Available' END, 'n/a', 0
            FROM #n;
            INSERT INTO dbo.[{mtu}] (Id, NId, CreatedOn, LastUpdatedOn, MaterialNId, Code, Kind, Quantity, Status, EquipmentNId, InContainment)
            SELECT NEWID(), CONCAT('B{f}-C', i), @now, @now, 'NV-WIP-CELL-{f}', CONCAT('C', i), 'Serial', 0, 'Available', 'n/a', 0
            FROM #n;
            INSERT INTO dbo.[{mtu}] (Id, NId, CreatedOn, LastUpdatedOn, MaterialNId, Code, Kind, Quantity, Status, EquipmentNId, InContainment)
            SELECT NEWID(), CONCAT('B{f}-E', i), @now, @now, 'NV-WIP-ELEC-{f}', CONCAT('E', i), 'Batch', 0, 'Available', 'n/a', 0
            FROM #n WHERE i < {(cells / 100) + 1};
            INSERT INTO dbo.[{mtu}] (Id, NId, CreatedOn, LastUpdatedOn, MaterialNId, Code, Kind, Quantity, Status, EquipmentNId, InContainment)
            SELECT NEWID(), CONCAT('B{f}-L', i), @now, @now, CASE WHEN i % 2 = 0 THEN 'NV-RM-SEP' ELSE 'NV-RM-CASE-{f}' END, CONCAT('L', i), 'Batch', 0,
                   'Available', 'n/a', 0
            FROM #n WHERE i < {2 * batches};

            INSERT INTO dbo.[{use}] (Id, NId, CreatedOn, LastUpdatedOn, WorkOrderNId, ProductMtuNId, ComponentMtuNId, Quantity)
            SELECT NEWID(), CONCAT('B{f}-UM', i), @now, @now, 'WO-SIM', CONCAT('B{f}-M', i / {cellsPerModule}), CONCAT('B{f}-F', i), 1 FROM #n;
            INSERT INTO dbo.[{use}] (Id, NId, CreatedOn, LastUpdatedOn, WorkOrderNId, ProductMtuNId, ComponentMtuNId, Quantity)
            SELECT NEWID(), CONCAT('B{f}-UF', i), @now, @now, 'WO-SIM', CONCAT('B{f}-F', i), CONCAT('B{f}-C', i), 1 FROM #n;
            INSERT INTO dbo.[{use}] (Id, NId, CreatedOn, LastUpdatedOn, WorkOrderNId, ProductMtuNId, ComponentMtuNId, Quantity)
            SELECT NEWID(), CONCAT('B{f}-UE', i), @now, @now, 'WO-SIM', CONCAT('B{f}-C', i), CONCAT('B{f}-E', i / 100), 1.2 FROM #n;
            INSERT INTO dbo.[{use}] (Id, NId, CreatedOn, LastUpdatedOn, WorkOrderNId, ProductMtuNId, ComponentMtuNId, Quantity)
            SELECT NEWID(), CONCAT('B{f}-US', i), @now, @now, 'WO-SIM', CONCAT('B{f}-C', i), CONCAT('B{f}-L', 2 * (i / {cells / batches})), 1 FROM #n;
            INSERT INTO dbo.[{use}] (Id, NId, CreatedOn, LastUpdatedOn, WorkOrderNId, ProductMtuNId, ComponentMtuNId, Quantity)
            SELECT NEWID(), CONCAT('B{f}-UC', i), @now, @now, 'WO-SIM', CONCAT('B{f}-C', i), CONCAT('B{f}-L', 2 * (i / {cells / batches}) + 1), 1 FROM #n;

            INSERT INTO dbo.[{nc}] (Id, NId, CreatedOn, LastUpdatedOn, Status, Type, Severity)
            SELECT NEWID(), CONCAT('B{f}-NC', i), @now, @now, 'OPEN', 'QUALITY', 'LOW' FROM #n WHERE i % 997 = 13;
            INSERT INTO dbo.[{item}] (Id, NId, CreatedOn, LastUpdatedOn, NonConformanceNId, MtuNId)
            SELECT NEWID(), CONCAT('B{f}-NC', i, '|B{f}-F', i), @now, @now, CONCAT('B{f}-NC', i), CONCAT('B{f}-F', i) FROM #n WHERE i % 997 = 13;
            """);
        return [.. Enumerable.Range(0, pallets).Select(i => $"B{f}-P{i}")];
    }

    private static int Held(string connection, string pallet)
    {
        using var sql = new SqlConnection(connection);
        sql.Open();
        using var command = new SqlCommand("nv_PalletHeldItems", sql) { CommandType = System.Data.CommandType.StoredProcedure, CommandTimeout = 120 };
        command.Parameters.AddWithValue("@PalletNId", pallet);
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            count++;
        }

        return count;
    }

    private static long Scalar(string connection, string sql)
    {
        using var c = new SqlConnection(connection);
        c.Open();
#pragma warning disable CA2100 // SQL của chính test
        using var command = new SqlCommand(sql, c);
#pragma warning restore CA2100
        return (long)command.ExecuteScalar()!;
    }

    private static void Exec(string connection, string sql)
    {
        using var c = new SqlConnection(connection);
        c.Open();
#pragma warning disable CA2100 // SQL của chính test, tên bảng từ registry
        using var command = new SqlCommand(sql, c) { CommandTimeout = 1800 };
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }
}
