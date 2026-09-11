using System.Globalization;
using Nvm.Kernel.Identity;

namespace Nvm.App.Execution;

/// <summary>Một production unit trong fixture M4, đủ ngữ cảnh cho POM read model và cho context SQL của C05.</summary>
public sealed record OperatorFixtureUnit
{
    public required string Id { get; init; }

    public required string SiteId { get; init; }

    public required string SerialNumber { get; init; }

    public required string UnitKind { get; init; }

    public required string Line { get; init; }

    public required string Resource { get; init; }

    public required string EquipmentPath { get; init; }

    public required string WorkOrderId { get; init; }

    public required string OperationRunId { get; init; }

    public required string StepCode { get; init; }

    public required string ExecutionState { get; init; }

    public required string QualityState { get; init; }

    public required string LocationState { get; init; }

    public string? BlockingReasonCode { get; init; }

    public string? BlockingReasonText { get; init; }

    public required int Revision { get; init; }
}

/// <summary>Một dòng WIP đã gộp sẵn: số unit theo (site, line, step, quality state).</summary>
public sealed record OperatorFixtureWip
{
    public required string Id { get; init; }

    public required string SiteId { get; init; }

    public required string Line { get; init; }

    public required string StepCode { get; init; }

    public required string QualityState { get; init; }

    public required int UnitCount { get; init; }

    public required int Revision { get; init; }
}

/// <summary>
/// Generator xác định (deterministic) cho fixture Operator Station của M4.
/// </summary>
/// <remarks>
/// <para>
/// Đây là <b>một nguồn duy nhất</b>: seed POM (C03) và adapter SQL sau này (C05) cùng đọc ra records ở
/// đây, không sinh hai tập dữ liệu độc lập rồi phải khớp thủ công. Không có ngẫu nhiên, không có đồng hồ
/// — cùng đầu vào cho cùng 1.000 unit/site để test đối chiếu được từng con số.
/// </para>
/// <para>
/// Catalog revision 3: trạm <c>NOVAVOLT/{site}/PACK/P1/EOL-01</c> tồn tại ở cả NV1 và DE1; DE1 là nhà máy
/// pack nên không có FORMATION (glossary factory model r3). WIP gộp <b>mọi</b> unit theo quality state để
/// tổng các dòng của một site bằng đúng số unit của site đó.
/// </para>
/// </remarks>
public static class OperatorFixture
{
    /// <summary>Số unit của mỗi site — con số M4 dùng để đo, không phải quy mô cả nhà máy.</summary>
    public const int UnitsPerSite = 1000;

    // Revision khớp catalog r3 mà resource path tham chiếu; cũng là giá trị ETag của snapshot.
    private const int CatalogRevision = 3;

    private const char YearDigit = '6'; // 2026

    private sealed record Station(
        string Area, string Line, string Resource, string StepCode, char KindChar, string UnitKind, int DayOfYear, int Count);

    // Thứ tự station cố định để work order và serial suy ra được và ổn định giữa các lần chạy.
    private static readonly (string Site, Station[] Stations)[] Sites =
    [
        ("NV1",
        [
            new("ASSEMBLY", "L1", "STACK-01", "STACK", 'C', "Cell", 210, 300),
            new("FORMATION", "F1", "FORM-01", "FORM", 'C', "Cell", 220, 200),
            new("MODULE", "M1", "MLOAD-01", "MLOAD", 'M', "Module", 230, 200),
            new("PACK", "P1", "PLOAD-01", "PLOAD", 'P', "Pack", 240, 150),
            new("PACK", "P1", "EOL-01", "EOL", 'P', "Pack", 250, 150),
        ]),
        ("DE1",
        [
            // Không FORMATION cho DE1 — nhà máy pack; module và pack thôi.
            new("MODULE", "M1", "MLOAD-01", "MLOAD", 'M', "Module", 230, 400),
            new("PACK", "P1", "PLOAD-01", "PLOAD", 'P', "Pack", 240, 300),
            new("PACK", "P1", "EOL-01", "EOL", 'P', "Pack", 250, 300),
        ]),
    ];

    /// <summary>Sinh toàn bộ 2.000 unit (1.000/site). Serial được xác thực qua <see cref="SerialNumber"/>.</summary>
    public static IReadOnlyList<OperatorFixtureUnit> GenerateUnits()
    {
        var units = new List<OperatorFixtureUnit>(UnitsPerSite * Sites.Length);
        var workOrderOrdinal = 0;
        foreach (var (site, stations) in Sites)
        {
            var produced = 0;
            foreach (var station in stations)
            {
                workOrderOrdinal++;
                var workOrderId = FormattableString.Invariant($"WO-2026-{workOrderOrdinal:0000}");
                var unitOrdinal = 0;
                for (var i = 0; i < station.Count; i++)
                {
                    unitOrdinal++;
                    var sequence = unitOrdinal.ToString("00000", CultureInfo.InvariantCulture);
                    // Cell đã có serial ở STACK/L1; đi tới FORM/F1 không đổi mã đã khắc.
                    var birthLine = station.KindChar == 'C' ? "L1" : station.Line;
                    var serial = FormattableString.Invariant(
                        $"{site}{station.KindChar}{birthLine}{YearDigit}{station.DayOfYear:000}A{sequence}");
                    // Kiểm bằng parser thật: fixture không được chứa serial mà scanner đọc lại sẽ từ chối.
                    if (!SerialNumber.TryParse(serial, out _))
                    {
                        throw new InvalidOperationException($"Fixture produced an invalid serial: {serial}");
                    }

                    var state = StateFor(i);
                    units.Add(new OperatorFixtureUnit
                    {
                        Id = FormattableString.Invariant($"{site}-U{(produced + i + 1):000000}"),
                        SiteId = site,
                        SerialNumber = serial,
                        UnitKind = station.UnitKind,
                        Line = station.Line,
                        Resource = station.Resource,
                        EquipmentPath = FormattableString.Invariant($"NOVAVOLT/{site}/{station.Area}/{station.Line}/{station.Resource}"),
                        WorkOrderId = workOrderId,
                        OperationRunId = FormattableString.Invariant($"OPRUN-{site}-{station.StepCode}-{unitOrdinal:0000}"),
                        StepCode = station.StepCode,
                        ExecutionState = state.Execution,
                        QualityState = state.Quality,
                        LocationState = state.Location,
                        BlockingReasonCode = state.BlockingCode,
                        BlockingReasonText = state.BlockingText,
                        Revision = CatalogRevision,
                    });
                }

                produced += station.Count;
            }

            if (produced != UnitsPerSite)
            {
                throw new InvalidOperationException($"Site {site} produced {produced} units, expected {UnitsPerSite}.");
            }
        }

        return units;
    }

    /// <summary>Gộp WIP từ chính các unit vừa sinh, nên số đếm luôn khớp ProductionUnits theo định nghĩa.</summary>
    public static IReadOnlyList<OperatorFixtureWip> BuildWipRows(IReadOnlyList<OperatorFixtureUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        return
        [
            .. units
                .GroupBy(unit => (unit.SiteId, unit.Line, unit.StepCode, unit.QualityState))
                .Select(group => new OperatorFixtureWip
                {
                    Id = FormattableString.Invariant($"{group.Key.SiteId}-{group.Key.Line}-{group.Key.StepCode}-{group.Key.QualityState}"),
                    SiteId = group.Key.SiteId,
                    Line = group.Key.Line,
                    StepCode = group.Key.StepCode,
                    QualityState = group.Key.QualityState,
                    UnitCount = group.Count(),
                    Revision = CatalogRevision,
                })
                .OrderBy(row => row.Id, StringComparer.Ordinal),
        ];
    }

    // Chu kỳ 10 cho mỗi station (mọi Count là bội của 10) để tổng từng nhóm quality state ra số tròn,
    // và để mỗi station phủ đủ: happy path Running/Pending, bị chặn Held, Scrapped, và non-running.
    private static (string Execution, string Quality, string Location, string? BlockingCode, string? BlockingText) StateFor(int index) =>
        (index % 10) switch
        {
            6 => ("Running", "Held", "AtRack-HOLD", "QUALITY_HOLD",
                "Unit đang bị giữ chất lượng; thao tác sản xuất bị chặn."),
            7 => ("Completed", "Released", "AtStation", "OPERATION_NOT_RUNNING",
                "Thao tác đã hoàn tất; không nhận thêm kết quả đo cho lần chạy này."),
            8 => ("Aborted", "Scrapped", "AtRack-SCRAP", "SCRAPPED",
                "Unit đã bị loại bỏ; không nhận thêm kết quả đo."),
            9 => ("Scheduled", "Pending", "AtStation", "OPERATION_NOT_RUNNING",
                "Thao tác chưa bắt đầu; chưa thể nhập kết quả đo."),
            _ => ("Running", "Pending", "AtStation", null, null),
        };
}
