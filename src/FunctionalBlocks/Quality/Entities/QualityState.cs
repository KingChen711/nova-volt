namespace Nvm.Quality.Entities;

/// <summary>Trạng thái chất lượng của unit, độc lập với execution và vị trí vật lý (scope §6.2).</summary>
public enum QualityState { Pending, Released, Held, Rework, Scrapped }

public static class QualityReasonCodes
{
    public const string QualityHold = "QUALITY_HOLD";
    public const string Scrapped = "SCRAPPED";
    public const string DuplicateSerial = "DUPLICATE_SERIAL";
}
