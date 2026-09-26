namespace Nvm.ProductionExecution.Entities;

/// <summary>Trạng thái của quá trình formation + aging một cell (scope §9/M7).</summary>
public enum FormationAgingState { Forming, Degassing, Aging, AwaitingMeasurement, Completed, Faulted, Quarantined }

public static class FormationAgingRules
{
    /// <summary>Formation phải xong trong 36 giờ, nếu không quá trình chuyển Faulted.</summary>
    public static readonly TimeSpan FormationTimeout = TimeSpan.FromHours(36);

    /// <summary>Aging kéo dài 10 ngày trước khi đo OCV lần 2.</summary>
    public static readonly TimeSpan AgingPeriod = TimeSpan.FromDays(10);

    /// <summary>Drift OCV quá 15 mV sau aging là nghi tự phóng điện: quarantine + NCR.</summary>
    public const decimal DriftLimitMillivolt = 15m;

    public const string FormationTimeoutKind = "FormationTimeout";
    public const string AgingDueKind = "AgingDue";

    public static bool IsActive(FormationAgingState state) =>
        state is not (FormationAgingState.Completed or FormationAgingState.Faulted or FormationAgingState.Quarantined);
}

/// <summary>
/// Một quá trình sống nhiều ngày cho một cell. Không phụ thuộc hạ tầng: chuyển trạng thái trả về mã lý do
/// khi không hợp lệ, và timeout chỉ có hiệu lực khi đúng trạng thái đang chờ nó.
/// </summary>
public sealed record FormationAgingProcess(
    string SiteId, string SerialNumber, FormationAgingState State, long Version, string TrayId, int Channel,
    string EquipmentPath, DateTimeOffset FormationDueAt, decimal? CapacityAh = null, decimal? Ocv1Millivolt = null,
    string? RackId = null, int? Level = null, int? AgingChannel = null, DateTimeOffset? AgingDueAt = null,
    decimal? Ocv2Millivolt = null)
{
    public static FormationAgingProcess Start(string siteId, string serialNumber, string trayId, int channel,
        string equipmentPath, DateTimeOffset startedAt) =>
        new(siteId, serialNumber, FormationAgingState.Forming, 0, trayId, channel, equipmentPath,
            startedAt + FormationAgingRules.FormationTimeout);

    public string? CanCompleteFormation() =>
        State == FormationAgingState.Forming ? null : FormationReasonCodes.InvalidState;

    public string? CanStartAging() =>
        State == FormationAgingState.Degassing ? null : FormationReasonCodes.InvalidState;

    public string? CanRecordOcv2() => State switch
    {
        FormationAgingState.AwaitingMeasurement => null,
        FormationAgingState.Aging => FormationReasonCodes.AgingNotElapsed,
        _ => FormationReasonCodes.InvalidState
    };

    /// <summary>Timeout chỉ áp dụng khi quá trình vẫn đang chờ đúng loại đó; nếu không nó là no-op.</summary>
    public bool TimeoutApplies(string kind) => kind switch
    {
        FormationAgingRules.FormationTimeoutKind => State == FormationAgingState.Forming,
        FormationAgingRules.AgingDueKind => State == FormationAgingState.Aging,
        _ => false
    };

    public static decimal Drift(decimal ocv1, decimal ocv2) => Math.Abs(ocv1 - ocv2);
}

public static class FormationReasonCodes
{
    public const string UnitNotFound = "UNIT_NOT_FOUND";
    public const string AlreadyInProcess = "ALREADY_IN_PROCESS";
    public const string ProcessNotFound = "PROCESS_NOT_FOUND";
    public const string InvalidState = "INVALID_PROCESS_STATE";
    public const string AgingNotElapsed = "AGING_NOT_ELAPSED";
    public const string QualityHold = "QUALITY_HOLD";
    public const string FormationTimeout = "FORMATION_TIMEOUT";
    public const string OcvDrift = "OCV_DRIFT";
}
