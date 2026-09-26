using System.Globalization;

namespace Nvm.Material.Entities;

public enum LotQuality { Pending, Released, Rejected }

/// <summary>Một lot vật liệu mua vào (scope §6.9). Cuộn điện cực tự sản xuất đi theo luật của cuộn, không qua đây.</summary>
public sealed record MaterialLot(string LotId, string MaterialCode, decimal Remaining, string UnitOfMeasure,
    DateTimeOffset ReceivedAt, DateTimeOffset? ExpiresAt, int? MaxExposureMinutes, DateTimeOffset? OpenedAt,
    LotQuality Quality, long StreamVersion);

/// <summary>Một lần cho phép dùng lot vượt luật, có hạn và có người duyệt.</summary>
public sealed record MaterialOverride(string OverrideId, string LotId, string Rule, DateTimeOffset ValidUntil);

/// <summary>Kết quả kiểm lot trước khi tiêu hao: mã luật bị vi phạm và câu cho người vận hành.</summary>
public sealed record LotViolation(string Rule, string Text);

/// <summary>
/// Luật tiêu hao phải chặn ở phần mềm, không chỉ cảnh báo (scope §6.9). Mỗi luật có thể được vượt bằng một override
/// còn hiệu lực — trừ số lượng và đơn vị, không override được.
/// </summary>
public static class MaterialRules
{
    public const string NotReleased = "LOT_NOT_RELEASED";
    public const string Expired = "LOT_EXPIRED";
    public const string ExposureExceeded = "EXPOSURE_EXCEEDED";
    public const string Fifo = "FIFO_VIOLATION";
    public const string UomMismatch = "UOM_MISMATCH";
    public const string Insufficient = "INSUFFICIENT_QUANTITY";
    public static readonly string[] Overridable = [Expired, ExposureExceeded, Fifo];

    public static LotViolation? Check(MaterialLot lot, decimal quantity, string unitOfMeasure, DateTimeOffset now,
        MaterialLot? olderAvailable, IReadOnlyCollection<MaterialOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(lot);
        ArgumentNullException.ThrowIfNull(overrides);
        if (!string.Equals(lot.UnitOfMeasure, unitOfMeasure, StringComparison.Ordinal))
        {
            // Không tự quy đổi (kg ≠ g): sai đơn vị là dấu hiệu master data hoặc nhập liệu sai.
            return new(UomMismatch, $"Đơn vị {unitOfMeasure} không khớp đơn vị của lot ({lot.UnitOfMeasure}).");
        }
        if (lot.Quality != LotQuality.Released)
        { return new(NotReleased, "Lot chưa được Quality cho phép dùng."); }
        if (quantity > lot.Remaining)
        {
            return new(Insufficient, string.Create(CultureInfo.InvariantCulture,
                $"Lot chỉ còn {lot.Remaining:0.###} {lot.UnitOfMeasure}, cần {quantity:0.###}."));
        }
        bool Allowed(string rule) => overrides.Any(o => o.Rule == rule && o.ValidUntil > now);
        if (lot.ExpiresAt is { } expires && expires <= now && !Allowed(Expired))
        {
            return new(Expired, string.Create(CultureInfo.InvariantCulture,
                $"Lot hết hạn lúc {expires:yyyy-MM-dd HH:mm} UTC."));
        }
        if (lot.OpenedAt is { } opened && lot.MaxExposureMinutes is { } limit && !Allowed(ExposureExceeded))
        {
            var exposed = now - opened;
            var max = TimeSpan.FromMinutes(limit);
            if (exposed > max)
            { return new(ExposureExceeded, $"Lot đã mở {Duration(exposed)} / giới hạn {Duration(max)}."); }
        }
        if (olderAvailable is not null && !Allowed(Fifo))
        { return new(Fifo, $"Còn lot cũ hơn ({olderAvailable.LotId}) phải dùng trước (FIFO)."); }
        return null;
    }

    /// <summary>Định dạng "4h32m" — đúng chữ người vận hành đọc trên màn hình.</summary>
    public static string Duration(TimeSpan value) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}h{value.Minutes:D2}m");
}
