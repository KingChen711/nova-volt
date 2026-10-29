using Npgsql;

namespace Nvm.Shopfloor;

/// <summary>Kịch bản của một line trong một khoảng thời gian: bao nhiêu cuộn điện cực, bao nhiêu cell, tin lỗi, hao hụt.</summary>
public sealed record LineScenario
{
    /// <summary>1–2: họ A (12 cell mỗi module); 3: họ B 60 cell; 4: họ B 68 cell.</summary>
    public int Line { get; init; } = 1;

    public DateTimeOffset Start { get; init; }

    /// <summary>Tiền tố của mọi serial và số lô trong lần chạy, để hai lần chạy không đụng nhau.</summary>
    public string Prefix { get; init; } = "S";

    public int Coils { get; init; } = 2;

    public decimal CoilLength { get; init; } = 120m;

    public int Cells { get; init; } = 24;

    /// <summary>Cứ mỗi N cell thì một cell bị máy lắp đánh KO (không qua formation). 0: không có.</summary>
    public int KoEvery { get; init; }

    /// <summary>Máy lắp quét được nhãn lô màng ngăn và vỏ. Tắt: tin không kèm lô, MOM trừ từ MTU tạm.</summary>
    public bool ScanComponentLots { get; init; }

    /// <summary>Số tin hao hụt lô nguyên liệu ở khu điện cực.</summary>
    public int ShrinkLogs { get; init; }

    /// <summary>Lô nguyên liệu bột trên tin phủ có kèm lượng thực dùng. Tắt: MOM trừ theo định mức.</summary>
    public bool ReportPowderQuantity { get; init; }

    /// <summary>
    /// Chạy tiếp tới đóng gói: họ A ghép hai module thành kit rồi cho vào tote, họ B cho module vào tote; đủ
    /// <see cref="TotesPerPallet"/> tote thì xếp một pallet. Tote lẻ cuối ca không lên pallet.
    /// </summary>
    public bool Pack { get; init; }

    /// <summary>Số tote một pallet; rỗng: số chuẩn của họ (A 8, B 6).</summary>
    public int? TotesPerPallet { get; init; }

    /// <summary>Họ A: đo cuối từng kit đã lên pallet (sau khi xếp pallet).</summary>
    public bool EolvTest { get; init; }
}

/// <summary>Số liệu mà MOM phải có sau khi ghi hết tin của một kịch bản.</summary>
public sealed record LineScenarioExpectation(
    int Coils,
    int Cells,
    int KoCells,
    int FormedCells,
    int Modules,
    int CellsPerModule,
    int LotMtus,
    int TemporaryMtus,
    int Consumptions,
    decimal CoatedLength,
    int ShrinkRows,
    int Kits = 0,
    int Totes = 0,
    int Pallets = 0,
    int TotesPerPallet = 0,
    int EolvRows = 0)
{
    /// <summary>MTU sản phẩm: cuộn, cell, cell đã formation, module, kit.</summary>
    public int ProductMtus => Coils + Cells + FormedCells + Modules + Kits;

    /// <summary>Dòng tin sản phẩm (không kể hao hụt): mỗi tote một tin đóng tote, mỗi pallet một tin xếp pallet.</summary>
    public int UnitRows => Coils + Cells + FormedCells + Modules + Totes + Pallets + EolvRows;
}

/// <summary>
/// Line simulator: đóng vai máy, ghi bảng máy của LineDB cho cả chuỗi điện cực → cell → formation → module. Trigger của từng
/// bảng chép sang bảng trung gian; simulator không biết gì về MOM.
/// </summary>
public sealed class LineSimulator(string connectionString)
{
    public static int CellsPerModule(int line) => line switch
    {
        3 => 60,
        4 => 68,
        _ => 12,
    };

    public static string Family(int line) => line <= 2 ? "A" : "B";

    public static int TotesPerPallet(int line) => line <= 2 ? 8 : 6;

    public LineScenarioExpectation Run(LineScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var family = Family(scenario.Line);
        var perModule = CellsPerModule(scenario.Line);
        var id = $"{scenario.Prefix}L{scenario.Line}";
        var cellsPerCoil = Math.Max(1, (int)Math.Floor(scenario.CoilLength / 1.2m));

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var rows = new LineRows(connection, transaction);

        var coils = Enumerable.Range(1, scenario.Coils).Select(k => $"{id}-EC{k:000}").ToList();
        for (var k = 0; k < coils.Count; k++)
        {
            var quantity = scenario.ReportPowderQuantity ? 0.5m * scenario.CoilLength : (decimal?)null;
            rows.Coil(scenario.Line, coils[k], scenario.CoilLength, $"AN-{id}-{k + 1:000}", $"CA-{id}-{k + 1:000}", quantity,
                scenario.Start.AddMinutes((5 * k) + 4));
        }

        for (var s = 0; s < scenario.ShrinkLogs; s++)
        {
            rows.Shrink(scenario.Line, $"AN-{id}-{(s % Math.Max(1, scenario.Coils)) + 1:000}", scenario.Start.AddMinutes(20 + s));
        }

        var good = new List<string>();
        var ko = 0;
        for (var i = 0; i < scenario.Cells; i++)
        {
            var serial = $"{id}-C{i + 1:0000}";
            var bad = scenario.KoEvery > 0 && (i + 1) % scenario.KoEvery == 0;
            rows.Cell(scenario.Line, family, serial, coils[Math.Min(i / cellsPerCoil, coils.Count - 1)], bad,
                scenario.ScanComponentLots ? id : null, scenario.Start.AddMinutes(30 + i));
            if (bad)
            {
                ko++;
            }
            else
            {
                good.Add(serial);
            }
        }

        for (var i = 0; i < good.Count; i++)
        {
            rows.Formation(scenario.Line, family, good[i], scenario.Start.AddHours(3).AddMinutes(i));
        }

        var modules = good.Count / perModule;
        var moduleIds = new List<string>();
        for (var j = 0; j < modules; j++)
        {
            moduleIds.Add($"{id}-M{j + 1:000}");
            rows.Module(scenario.Line, family, moduleIds[j], good.Skip(j * perModule).Take(perModule).ToArray(),
                scenario.Start.AddHours(8).AddMinutes(2 * j));
        }

        var (kits, totes, pallets, eolv) = scenario.Pack ? Pack(rows, scenario, family, id, moduleIds) : (0, 0, 0, 0);
        transaction.Commit();

        var temporary = scenario.ScanComponentLots ? 0 : 2 * scenario.Cells;
        var lotMtus = (2 * scenario.Coils) + (scenario.ScanComponentLots ? 2 : 0);
        var consumptions = (2 * scenario.Coils) + (3 * scenario.Cells) + good.Count + (modules * perModule) + (2 * kits);
        return new LineScenarioExpectation(scenario.Coils, scenario.Cells, ko, good.Count, modules, perModule, lotMtus, temporary,
            consumptions, scenario.Coils * scenario.CoilLength, scenario.ShrinkLogs, kits, totes, pallets,
            scenario.TotesPerPallet ?? TotesPerPallet(scenario.Line), eolv);
    }

    /// <summary>Đóng tote (kit với họ A), xếp pallet đủ, rồi đo cuối kit đã lên pallet.</summary>
    private static (int Kits, int Totes, int Pallets, int Eolv) Pack(LineRows rows, LineScenario scenario, string family, string id,
        List<string> modules)
    {
        var perPallet = scenario.TotesPerPallet ?? TotesPerPallet(scenario.Line);
        var start = scenario.Start.AddHours(9);
        List<string> units = family == "A"
            ? [.. Enumerable.Range(0, modules.Count / 2).Select(k => $"{id}-K{k + 1:000}")]
            : modules;
        var totes = new List<string>();
        for (var t = 0; t < units.Count; t++)
        {
            totes.Add($"{id}-T{t + 1:000}");
            rows.Pack(scenario.Line, family, units[t], totes[t], family == "A" ? [modules[2 * t], modules[(2 * t) + 1]] : null,
                start.AddMinutes(t));
        }

        var pallets = totes.Count / perPallet;
        for (var p = 0; p < pallets; p++)
        {
            rows.Pallet(scenario.Line, $"{id}-P{p + 1:00}", [.. totes.Skip(p * perPallet).Take(perPallet)], start.AddHours(1).AddMinutes(p));
        }

        var tested = family == "A" && scenario.EolvTest ? pallets * perPallet : 0;
        for (var k = 0; k < tested; k++)
        {
            rows.Eolv(scenario.Line, units[k], start.AddHours(2).AddMinutes(k));
        }

        return (family == "A" ? units.Count : 0, totes.Count, pallets, tested);
    }
}
