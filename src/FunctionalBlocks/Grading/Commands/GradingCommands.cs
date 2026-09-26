using System.Collections.Immutable;
using System.Globalization;
using Nvm.Contracts.Events.Grading;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;
using Nvm.Kernel.Identity;

namespace Nvm.Grading.Commands;

public static class GradingReasonCodes
{
    public const string InvalidRuleSet = "INVALID_RULE_SET";
    public const string RuleSetExists = "RULE_SET_EXISTS";
    public const string RuleSetNotFound = "RULE_SET_NOT_FOUND";
    public const string AlreadyApproved = "RULE_SET_ALREADY_APPROVED";
    public const string SeparationOfDuties = "SEPARATION_OF_DUTIES";
    public const string EffectiveDateTaken = "EFFECTIVE_DATE_TAKEN";
    public const string NoRuleSet = "NO_EFFECTIVE_RULE_SET";
    public const string UnitNotFound = "UNIT_NOT_FOUND";
    public const string NotGraded = "NOT_GRADED";
    public const string SameRuleSet = "SAME_RULE_SET";
}

/// <summary>Lưu bản nháp rule set; chưa có hiệu lực cho tới khi được người khác duyệt.</summary>
public sealed record DefineGradingRuleSetCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string RuleSetId, int Version, string ProductCode, DateTimeOffset EffectiveFrom,
    ImmutableArray<GradingBin> Bins, ImmutableArray<GradingReject> Rejects)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "DefineGradingRuleSet";
    public override string CanonicalPayload => Canonical(RuleSetId, Version.ToString(CultureInfo.InvariantCulture),
        ProductCode, EffectiveFrom.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        new Entities.GradingRuleSet(RuleSetId, Version, ProductCode, EffectiveFrom, Bins, Rejects).ContentSha256());
}

public sealed record ApproveGradingRuleSetCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string RuleSetId, int Version)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "ApproveGradingRuleSet";
    public override string CanonicalPayload => Canonical(RuleSetId, Version.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Grade một cell từ bộ số đo mới (một measurement mới).</summary>
public sealed record GradeUnitCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string SerialNumber, decimal CapacityAh, decimal OcvMillivolt, decimal DcirMilliOhm, decimal? OcvDriftMillivolt)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "GradeUnit";
    public override string CanonicalPayload => Canonical(SerialNumber, Number(CapacityAh), Number(OcvMillivolt),
        Number(DcirMilliOhm), Number(OcvDriftMillivolt));
}

/// <summary>Đánh giá lại measurement gần nhất theo rule set đang hiệu lực; không đo lại, không ghi đè.</summary>
public sealed record ReevaluateUnitCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string SerialNumber)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "ReevaluateUnit";
    public override string CanonicalPayload => Canonical(SerialNumber);
}

public sealed class GradingCommandValidator : ICommandValidator<DefineGradingRuleSetCommand>,
    ICommandValidator<ApproveGradingRuleSetCommand>, ICommandValidator<GradeUnitCommand>,
    ICommandValidator<ReevaluateUnitCommand>
{
    public IEnumerable<ValidationFailure> Validate(DefineGradingRuleSetCommand command) =>
        RuleSet(command.RuleSetId, command.Version).Concat(Text(command.ProductCode, "ProductCode", 100))
            .Concat(command.Bins.IsDefaultOrEmpty || command.Bins.Length > 100
                ? [new ValidationFailure("Bins", "Rule set phải có 1–100 bin.")] : []);

    public IEnumerable<ValidationFailure> Validate(ApproveGradingRuleSetCommand command) =>
        RuleSet(command.RuleSetId, command.Version);

    public IEnumerable<ValidationFailure> Validate(GradeUnitCommand command) =>
        Serial(command, command.SerialNumber)
            .Concat(command.CapacityAh is > 0 and < 1000 && command.OcvMillivolt is > 0 and < 5000 &&
                command.DcirMilliOhm is > 0 and < 100
                ? [] : [new ValidationFailure("Measurement", "Số đo nằm ngoài miền hợp lệ.")]);

    public IEnumerable<ValidationFailure> Validate(ReevaluateUnitCommand command) =>
        Serial(command, command.SerialNumber);

    private static IEnumerable<ValidationFailure> RuleSet(string? id, int version) =>
        Text(id, "RuleSetId", 50).Concat(version is >= 1 and <= 10_000 ? [] : [new ValidationFailure("Version", "Version phải từ 1.")]);

    private static IEnumerable<ValidationFailure> Serial(DurableCommand command, string serial) =>
        SerialNumber.TryParse(serial, out var parsed) && parsed.SiteCode == command.SiteId
            ? [] : [new ValidationFailure("SerialNumber", "Serial không hợp lệ hoặc không thuộc site.")];

    private static IEnumerable<ValidationFailure> Text(string? value, string field, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum
            ? [new ValidationFailure(field, $"{field} phải có 1–{maximum} ký tự.")] : [];
}
