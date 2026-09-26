using System.Globalization;
using Nvm.Equipment.Entities;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.Equipment.Commands;

public static class EquipmentReasonCodes
{
    public const string EquipmentNotFound = "EQUIPMENT_NOT_FOUND";
    public const string EquipmentExists = "EQUIPMENT_EXISTS";
    public const string AlreadyInState = "ALREADY_IN_STATE";
    public const string OutOfOrder = "STATE_CHANGE_OUT_OF_ORDER";
    public const string UnknownReason = "UNKNOWN_DOWNTIME_REASON";
    public const string ReasonNotLeaf = "DOWNTIME_REASON_NOT_LEAF";
    public const string NoIdealCycle = "NO_IDEAL_CYCLE_TIME";
    public const string CountWindowExists = "COUNT_WINDOW_EXISTS";
}

/// <summary>Đưa một máy vào quản lý OEE, bắt đầu ở trạng thái chạy.</summary>
public sealed record RegisterEquipmentCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string EquipmentPath, string EquipmentClass)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "RegisterEquipment";
    public override string CanonicalPayload => Canonical(EquipmentPath, EquipmentClass);
}

/// <summary>Máy chạy lại hoặc dừng (kèm mã lý do lá). <c>Time</c> là thời điểm máy đổi trạng thái.</summary>
public sealed record ChangeEquipmentStateCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string EquipmentPath, string State, string? ReasonCode)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "ChangeEquipmentState";
    public override string CanonicalPayload => Canonical(EquipmentPath, State, ReasonCode);
}

/// <summary>Sản lượng tổng và đạt của máy trong [WindowFrom, WindowTo).</summary>
public sealed record RecordProductionCountCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string EquipmentPath, string ProductCode, DateTimeOffset WindowFrom, DateTimeOffset WindowTo, long TotalCount, long GoodCount)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "RecordProductionCount";
    public override string CanonicalPayload => Canonical(EquipmentPath, ProductCode,
        WindowFrom.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        WindowTo.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        TotalCount.ToString(CultureInfo.InvariantCulture), GoodCount.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Master data: ideal cycle time cho (EquipmentClass, ProductCode), mỗi lần đặt là một version mới.</summary>
public sealed record SetIdealCycleTimeCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string EquipmentClass, string ProductCode, int CycleMilliseconds, DateTimeOffset EffectiveFrom)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "SetIdealCycleTime";
    public override string CanonicalPayload => Canonical(EquipmentClass, ProductCode,
        CycleMilliseconds.ToString(CultureInfo.InvariantCulture),
        EffectiveFrom.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}

public sealed class EquipmentCommandValidator : ICommandValidator<RegisterEquipmentCommand>,
    ICommandValidator<ChangeEquipmentStateCommand>, ICommandValidator<RecordProductionCountCommand>,
    ICommandValidator<SetIdealCycleTimeCommand>
{
    public IEnumerable<ValidationFailure> Validate(RegisterEquipmentCommand command) =>
        Text(command.EquipmentPath, "EquipmentPath", 200).Concat(Text(command.EquipmentClass, "EquipmentClass", 50));

    public IEnumerable<ValidationFailure> Validate(ChangeEquipmentStateCommand command)
    {
        foreach (var failure in Text(command.EquipmentPath, "EquipmentPath", 200))
        { yield return failure; }
        if (command.State is not (EquipmentStates.Running or EquipmentStates.Stopped))
        { yield return new ValidationFailure(nameof(command.State), "State phải là Running hoặc Stopped."); }
        if (command.State == EquipmentStates.Stopped && string.IsNullOrWhiteSpace(command.ReasonCode))
        { yield return new ValidationFailure(nameof(command.ReasonCode), "Dừng máy phải có mã lý do."); }
        if (command.State == EquipmentStates.Running && command.ReasonCode is not null)
        { yield return new ValidationFailure(nameof(command.ReasonCode), "Chạy lại không mang mã lý do."); }
    }

    public IEnumerable<ValidationFailure> Validate(RecordProductionCountCommand command)
    {
        foreach (var failure in Text(command.EquipmentPath, "EquipmentPath", 200).Concat(Text(command.ProductCode, "ProductCode", 100)))
        { yield return failure; }
        if (command.WindowTo <= command.WindowFrom || command.WindowTo - command.WindowFrom > TimeSpan.FromDays(1))
        { yield return new ValidationFailure(nameof(command.WindowTo), "Khoảng đếm phải dương và không quá một ngày."); }
        if (command.TotalCount < 0 || command.GoodCount < 0 || command.GoodCount > command.TotalCount)
        { yield return new ValidationFailure(nameof(command.GoodCount), "Số đạt phải từ 0 tới tổng số."); }
    }

    public IEnumerable<ValidationFailure> Validate(SetIdealCycleTimeCommand command) =>
        Text(command.EquipmentClass, "EquipmentClass", 50).Concat(Text(command.ProductCode, "ProductCode", 100))
            .Concat(command.CycleMilliseconds is >= 1 and <= 86_400_000 && command.EffectiveFrom != default
                ? [] : [new ValidationFailure("CycleMilliseconds", "Cycle time 1 ms – 1 ngày và thời điểm hiệu lực là bắt buộc.")]);

    private static IEnumerable<ValidationFailure> Text(string? value, string field, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum
            ? [new ValidationFailure(field, $"{field} phải có 1–{maximum} ký tự.")] : [];
}
