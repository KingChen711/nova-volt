using Nvm.Kernel.Identity;

namespace Nvm.Ingestion.RawCurves;

/// <summary>Định danh nhà máy và interval vật lý đi kèm với một file CSV formation chính xác.</summary>
public sealed record RawCurveDescriptor
{
    /// <summary>Tạo metadata cho một interval formation half-open.</summary>
    public RawCurveDescriptor(
        EquipmentPath equipmentPath,
        string? unitId,
        DateTimeOffset curveStartAt,
        DateTimeOffset curveEndAt)
    {
        ArgumentNullException.ThrowIfNull(equipmentPath);

        var siteId = equipmentPath.SiteId
            ?? throw new ArgumentException("A raw curve must belong to one plant.", nameof(equipmentPath));

        // Làm tròn về đúng mức mà evidence store thực sự lưu được, trước khi bất cứ gì được kiểm tra.
        // `timestamptz` chỉ giữ tới micro giây, nên một interval mà một ADR mô tả theo tick là một
        // interval mà archive không thể tái tạo lại: dòng trả về khác với descriptor đã ghi ra nó, và
        // identity check cho lần archive thứ hai của cùng một export khi đó sẽ báo "different plant
        // evidence" cho bằng chứng vốn giống hệt nhau. Tệ hơn, một export chỉ có một reading kết thúc
        // sau một tick 100 ns kể từ lúc bắt đầu, thứ mà PostgreSQL lưu thành không có duration nào cả
        // và từ chối -- nên export bình thường nhất mà một end-of-line tester ghi ra sẽ không bao giờ
        // archive được, và file cứ đi vòng vòng trong retry loop mãi mãi. Được phát hiện bởi
        // `scripts/file-drop-race-probe.sh` trên stack đang chạy, với 679 test xanh đứng sau nó.
        curveStartAt = ToStorablePrecision(curveStartAt);
        curveEndAt = ToStorablePrecision(curveEndAt);

        if (curveEndAt <= curveStartAt)
        {
            throw new ArgumentException(
                "The raw curve interval must have a positive duration once rounded to the "
                + "microsecond the archive stores.",
                nameof(curveEndAt));
        }

        if (unitId is not null && string.IsNullOrWhiteSpace(unitId))
        {
            throw new ArgumentException("Unit ID is either absent or non-empty.", nameof(unitId));
        }

        EquipmentPath = equipmentPath;
        SiteId = siteId;
        UnitId = unitId;
        CurveStartAt = curveStartAt.ToUniversalTime();
        CurveEndAt = curveEndAt.ToUniversalTime();
    }

    /// <summary>Nhà máy được suy ra từ equipment path, không bao giờ được nhận riêng (K3).</summary>
    public string SiteId { get; }

    /// <summary>Formation channel hoặc cycler đã ghi ra file.</summary>
    public EquipmentPath EquipmentPath { get; }

    /// <summary>Cell đang được formed khi serial của nó đã được biết.</summary>
    public string? UnitId { get; }

    /// <summary>Điểm bắt đầu inclusive của các measurement trong file.</summary>
    public DateTimeOffset CurveStartAt { get; }

    /// <summary>Điểm kết thúc exclusive của các measurement trong file.</summary>
    public DateTimeOffset CurveEndAt { get; }

    /// <summary>Bỏ bớt precision mà archive không lưu được, để descriptor là thứ nhận lại được đúng như vậy.</summary>
    private static DateTimeOffset ToStorablePrecision(DateTimeOffset instant) =>
        new(instant.Ticks - (instant.Ticks % TimeSpan.TicksPerMicrosecond), instant.Offset);
}
