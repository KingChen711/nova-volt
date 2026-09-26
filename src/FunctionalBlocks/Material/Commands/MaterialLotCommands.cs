using System.Collections.Immutable;
using System.Globalization;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;
using Nvm.Material.Entities;

namespace Nvm.Material.Commands;

public sealed record ReceiveMaterialLotCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string LotId, string MaterialCode, decimal Quantity, string UnitOfMeasure, DateTimeOffset? ExpiresAt,
    int? MaxExposureMinutes, string? SupplierLotId)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "ReceiveMaterialLot";
    public override string CanonicalPayload => Canonical(LotId, MaterialCode, Number(Quantity), UnitOfMeasure,
        ExpiresAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        MaxExposureMinutes?.ToString(CultureInfo.InvariantCulture), SupplierLotId);
}

public sealed record ReleaseMaterialLotCommand(string Site, string Actor, string Submission, DateTimeOffset Time, string LotId)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "ReleaseMaterialLot";
    public override string CanonicalPayload => Canonical(LotId);
}

public sealed record OpenMaterialLotCommand(string Site, string Actor, string Submission, DateTimeOffset Time, string LotId)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "OpenMaterialLot";
    public override string CanonicalPayload => Canonical(LotId);
}

/// <summary>Cho phép dùng lot vượt một luật (hết hạn, quá tiếp xúc, FIFO) tới <c>ValidUntil</c>; cần chữ ký QaManager.</summary>
public sealed record GrantMaterialOverrideCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string LotId, string Rule, string Justification, DateTimeOffset ValidUntil, ImmutableArray<string> SignatureIds)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "GrantMaterialOverride";
    public override string CanonicalPayload => Canonical([LotId, Rule, Justification,
        ValidUntil.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), .. SignatureIds.Order(StringComparer.Ordinal)]);

    /// <summary>Nội dung được ký: lot, luật, lý do và hạn.</summary>
    public string SignedContent => Nvm.Material.Handlers.MaterialLotProcessor.OverrideContent(LotId, Rule, Justification, ValidUntil);
}

public sealed class MaterialLotValidator : ICommandValidator<ReceiveMaterialLotCommand>,
    ICommandValidator<GrantMaterialOverrideCommand>
{
    public IEnumerable<ValidationFailure> Validate(ReceiveMaterialLotCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.LotId) || command.LotId.Length > 100 ||
            string.IsNullOrWhiteSpace(command.MaterialCode) || command.MaterialCode.Length > 100)
        { yield return new ValidationFailure(nameof(command.LotId), "Lot và mã vật liệu là bắt buộc."); }
        if (command.Quantity <= 0 || string.IsNullOrWhiteSpace(command.UnitOfMeasure) || command.UnitOfMeasure.Length > 20)
        { yield return new ValidationFailure(nameof(command.Quantity), "Số lượng dương và đơn vị là bắt buộc."); }
        if (command.MaxExposureMinutes is <= 0 or > 60 * 24 * 30)
        { yield return new ValidationFailure(nameof(command.MaxExposureMinutes), "Giới hạn tiếp xúc phải từ 1 phút tới 30 ngày."); }
    }

    public IEnumerable<ValidationFailure> Validate(GrantMaterialOverrideCommand command)
    {
        if (!MaterialRules.Overridable.Contains(command.Rule, StringComparer.Ordinal))
        { yield return new ValidationFailure(nameof(command.Rule), "Chỉ override được hết hạn, quá tiếp xúc hoặc FIFO."); }
        if (string.IsNullOrWhiteSpace(command.Justification) || command.Justification.Length > 500 ||
            command.SignatureIds.IsDefaultOrEmpty || command.ValidUntil <= command.OccurredAt)
        { yield return new ValidationFailure(nameof(command.Justification), "Cần lý do, chữ ký và hạn override sau thời điểm cấp."); }
    }
}
