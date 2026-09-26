namespace Nvm.Equipment.Entities;

public static class EquipmentStates
{
    public const string Running = "Running";
    public const string Stopped = "Stopped";
}

public static class DowntimeCategories
{
    public const string Planned = "Planned";
    public const string Unplanned = "Unplanned";
    public const string MicroStop = "MicroStop";
}

/// <summary>Một nút của cây lý do dừng. Chỉ lá mới được gán cho một lần dừng; nhánh chỉ để gom báo cáo.</summary>
public sealed record DowntimeReason(string Code, string? ParentCode, string Category, bool IsLeaf);

/// <summary>Trạng thái hiện tại của một máy và version của stream <c>equipment:{path}</c>.</summary>
public sealed record EquipmentState(string EquipmentPath, string EquipmentClass, string State, DateTimeOffset StateSince,
    string? ReasonCode, long StreamVersion);

/// <summary>Luật phân loại dừng của scope §6.10.</summary>
public static class DowntimeRules
{
    /// <summary>Dừng không kế hoạch ngắn hơn ngưỡng này là micro-stop, tính vào Performance chứ không vào Availability.</summary>
    public static readonly TimeSpan MicroStopThreshold = TimeSpan.FromMinutes(5);

    public static string Classify(string reasonCategory, TimeSpan duration) =>
        reasonCategory == DowntimeCategories.Planned ? DowntimeCategories.Planned
        : duration < MicroStopThreshold ? DowntimeCategories.MicroStop
        : DowntimeCategories.Unplanned;
}
