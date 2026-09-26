using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;
using Nvm.Kernel.Identity;

namespace Nvm.ProductionExecution.Commands;

public sealed record StartFormationCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string SerialNumber, string TrayId, int Channel, string EquipmentPath)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "StartFormation";
    public override string CanonicalPayload => Canonical(SerialNumber, TrayId, Channel.ToString(System.Globalization.CultureInfo.InvariantCulture), EquipmentPath);
}

public sealed record CompleteFormationCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string SerialNumber, decimal CapacityAh, string? CurveUri)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "CompleteFormation";
    public override string CanonicalPayload => Canonical(SerialNumber, Number(CapacityAh), CurveUri);
}

/// <summary>Degas xong: ghi OCV lần 1 và vị trí trong kho aging.</summary>
public sealed record StartAgingCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string SerialNumber, decimal Ocv1Millivolt, string RackId, int Level, int Channel)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "StartAging";
    public override string CanonicalPayload => Canonical(SerialNumber, Number(Ocv1Millivolt), RackId,
        Level.ToString(System.Globalization.CultureInfo.InvariantCulture), Channel.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed record RecordOcv2Command(string Site, string Actor, string Submission, DateTimeOffset Time,
    string SerialNumber, decimal Ocv2Millivolt)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "RecordOcv2";
    public override string CanonicalPayload => Canonical(SerialNumber, Number(Ocv2Millivolt));
}

/// <summary>Command nội bộ do worker timeout gửi; key suy từ (serial, loại, hạn) nên bắn lại là no-op.</summary>
public sealed record FireFormationTimeoutCommand(string Site, string SerialNumber, string Kind, DateTimeOffset DueAt)
    : DurableCommand(Site, "system:formation-timeout", $"{SerialNumber}:{Kind}:{DueAt.UtcTicks}", DueAt)
{
    public override string CommandType => "FireFormationTimeout";
    public override string CanonicalPayload => Canonical(SerialNumber, Kind);
}

public sealed class FormationCommandValidator :
    ICommandValidator<StartFormationCommand>, ICommandValidator<CompleteFormationCommand>,
    ICommandValidator<StartAgingCommand>, ICommandValidator<RecordOcv2Command>
{
    public IEnumerable<ValidationFailure> Validate(StartFormationCommand command) =>
        Common(command, command.SerialNumber).Concat(Text(command.TrayId, "TrayId", 50))
            .Concat(Text(command.EquipmentPath, "EquipmentPath", 200))
            .Concat(command.Channel is >= 1 and <= 512 ? [] : [new ValidationFailure("Channel", "Kênh phải từ 1 đến 512.")]);

    public IEnumerable<ValidationFailure> Validate(CompleteFormationCommand command) =>
        Common(command, command.SerialNumber)
            .Concat(command.CapacityAh is > 0 and < 1000 ? [] : [new ValidationFailure("CapacityAh", "Dung lượng phải trong (0, 1000) Ah.")]);

    public IEnumerable<ValidationFailure> Validate(StartAgingCommand command) =>
        Common(command, command.SerialNumber).Concat(Text(command.RackId, "RackId", 20)).Concat(Millivolt(command.Ocv1Millivolt))
            .Concat(command.Level is >= 1 and <= 50 && command.Channel is >= 1 and <= 1000 ? []
                : [new ValidationFailure("Level", "Level 1–50 và channel 1–1000.")]);

    public IEnumerable<ValidationFailure> Validate(RecordOcv2Command command) =>
        Common(command, command.SerialNumber).Concat(Millivolt(command.Ocv2Millivolt));

    private static IEnumerable<ValidationFailure> Common(DurableCommand command, string serial)
    {
        if (!SerialNumber.TryParse(serial, out var parsed) || parsed.SiteCode != command.SiteId || parsed.Kind != ProductionUnitKind.Cell)
        { yield return new ValidationFailure("SerialNumber", "Serial phải là cell thuộc site của command."); }
        if (string.IsNullOrWhiteSpace(command.SubmissionId) || command.SubmissionId.Length > 190 || command.OccurredAt == default)
        { yield return new ValidationFailure("SubmissionId", "Submission và thời điểm là bắt buộc."); }
    }

    private static IEnumerable<ValidationFailure> Text(string? value, string field, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        { yield return new ValidationFailure(field, $"{field} phải có 1–{maximum} ký tự."); }
    }

    private static IEnumerable<ValidationFailure> Millivolt(decimal value)
    {
        if (value is <= 0 or > 5000)
        { yield return new ValidationFailure("Ocv", "OCV phải trong (0, 5000] mV."); }
    }
}
