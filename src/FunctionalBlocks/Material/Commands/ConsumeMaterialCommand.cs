using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;
using Nvm.Kernel.Identity;

namespace Nvm.Material.Commands;

public static class MaterialReasonCodes
{
    public const string UnitNotFound = "UNIT_NOT_FOUND";
    public const string InvalidSpan = "INVALID_SPAN";
    public const string QualityHold = "QUALITY_HOLD";
    public const string Scrapped = "SCRAPPED";
    public const string LotOnHold = "LOT_ON_HOLD";
}

/// <summary>Lot vật liệu hoặc đoạn cuộn điện cực đi vào một unit (cạnh TRANSFORMATION).</summary>
/// <remarks>
/// <c>LotKind</c> là <c>Lot</c> hoặc <c>Roll</c>. Chỉ cuộn mới có khoảng mét [SpanFrom, SpanTo).
/// </remarks>
public sealed record ConsumeMaterialCommand(
    string Site, string Actor, string Submission, DateTimeOffset Time,
    string ConsumerSerialNumber, string LotId, string LotKind, string MaterialCode,
    decimal Quantity, string UnitOfMeasure, decimal? SpanFromMeter, decimal? SpanToMeter, string OperationRunId)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "ConsumeMaterial";

    public override string CanonicalPayload => Canonical(ConsumerSerialNumber, LotId, LotKind, MaterialCode,
        Number(Quantity), UnitOfMeasure, Number(SpanFromMeter), Number(SpanToMeter), OperationRunId);
}

public sealed class ConsumeMaterialValidator : ICommandValidator<ConsumeMaterialCommand>
{
    public IEnumerable<ValidationFailure> Validate(ConsumeMaterialCommand command)
    {
        if (!SerialNumber.TryParse(command.ConsumerSerialNumber, out var serial) || serial.SiteCode != command.SiteId)
        { yield return new ValidationFailure(nameof(command.ConsumerSerialNumber), "Serial unit tiêu thụ không hợp lệ hoặc không thuộc site."); }
        if (!Has(command.LotId, 100))
        { yield return new ValidationFailure(nameof(command.LotId), "LotId phải có 1–100 ký tự."); }
        if (command.LotKind is not ("Lot" or "Roll"))
        { yield return new ValidationFailure(nameof(command.LotKind), "LotKind phải là Lot hoặc Roll."); }
        if (!Has(command.MaterialCode, 100))
        { yield return new ValidationFailure(nameof(command.MaterialCode), "MaterialCode phải có 1–100 ký tự."); }
        if (command.Quantity <= 0)
        { yield return new ValidationFailure(nameof(command.Quantity), "Số lượng phải lớn hơn 0."); }
        if (!Has(command.UnitOfMeasure, 20))
        { yield return new ValidationFailure(nameof(command.UnitOfMeasure), "Đơn vị phải có 1–20 ký tự."); }
        if (!Has(command.OperationRunId, 100))
        { yield return new ValidationFailure(nameof(command.OperationRunId), "OperationRunId phải có 1–100 ký tự."); }
        if (!Has(command.SubmissionId, 190) || !Has(command.ActorId, 200) || command.OccurredAt == default)
        { yield return new ValidationFailure(nameof(command.SubmissionId), "Submission, actor và thời điểm là bắt buộc."); }
    }

    private static bool Has(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}
