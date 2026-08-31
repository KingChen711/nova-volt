using Nvm.Kernel.Identity;

namespace Nvm.Ingestion.RawCurves;

/// <summary>Plant identity and physical interval carried beside one exact formation CSV.</summary>
public sealed record RawCurveDescriptor
{
    /// <summary>Creates metadata for one half-open formation interval.</summary>
    public RawCurveDescriptor(
        EquipmentPath equipmentPath,
        string? unitId,
        DateTimeOffset curveStartAt,
        DateTimeOffset curveEndAt)
    {
        ArgumentNullException.ThrowIfNull(equipmentPath);

        var siteId = equipmentPath.SiteId
            ?? throw new ArgumentException("A raw curve must belong to one plant.", nameof(equipmentPath));

        // Rounded to what the evidence store can actually hold before anything is checked. `timestamptz`
        // keeps microseconds, so an interval an ADR describes in ticks is an interval the archive
        // cannot reproduce: the row that comes back differs from the descriptor that wrote it, and
        // the identity check for a second archival of the same export then reports "different plant
        // evidence" for evidence that is identical. Worse, an export of a single reading ended one
        // 100 ns tick after it began, which PostgreSQL stored as no duration at all and refused --
        // so the most ordinary export an end-of-line tester writes could never be archived, and the
        // file went round the retry loop for ever. Found by `scripts/file-drop-race-probe.sh` on the
        // running stack, with 679 green tests behind it.
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

    /// <summary>The plant derived from the equipment path, never accepted separately (K3).</summary>
    public string SiteId { get; }

    /// <summary>The formation channel or cycler that wrote the file.</summary>
    public EquipmentPath EquipmentPath { get; }

    /// <summary>The cell being formed when its serial is already known.</summary>
    public string? UnitId { get; }

    /// <summary>Inclusive beginning of the measurements in the file.</summary>
    public DateTimeOffset CurveStartAt { get; }

    /// <summary>Exclusive end of the measurements in the file.</summary>
    public DateTimeOffset CurveEndAt { get; }

    /// <summary>Drops precision the archive cannot store, so a descriptor is what comes back.</summary>
    private static DateTimeOffset ToStorablePrecision(DateTimeOffset instant) =>
        new(instant.Ticks - (instant.Ticks % TimeSpan.TicksPerMicrosecond), instant.Offset);
}
