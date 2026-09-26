using System.Collections.Immutable;
using System.Globalization;
using Nvm.Contracts.Events.Recipe;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.Recipe.Commands;

public sealed record DefineRecipeVersionCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string RecipeId, int Version, string ProductCode, string StepCode, string EquipmentClass,
    ImmutableArray<RecipeParameter> Parameters)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "DefineRecipeVersion";
    public override string CanonicalPayload => Canonical(RecipeId, Version.ToString(CultureInfo.InvariantCulture), ProductCode,
        StepCode, EquipmentClass, new Entities.RecipeVersion(RecipeId, Version, ProductCode, StepCode, EquipmentClass,
            Parameters).ContentSha256());
}

/// <summary>Duyệt và cho hiệu lực một version; cần chữ ký điện tử trên hash nội dung của version.</summary>
public sealed record ApproveRecipeVersionCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string RecipeId, int Version, DateTimeOffset EffectiveFrom, ImmutableArray<string> SignatureIds)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "ApproveRecipeVersion";
    public override string CanonicalPayload => Canonical([RecipeId, Version.ToString(CultureInfo.InvariantCulture),
        EffectiveFrom.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), .. SignatureIds.Order(StringComparer.Ordinal)]);
}

/// <summary>Ghi recipe đang hiệu lực đã được dùng cho một lần chạy trên một máy.</summary>
public sealed record ApplyRecipeCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string EquipmentPath, string EquipmentClass, string ProductCode, string StepCode, string OperationRunId, string? LotId)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "ApplyRecipe";
    public override string CanonicalPayload => Canonical(EquipmentPath, EquipmentClass, ProductCode, StepCode,
        OperationRunId, LotId);
}

public sealed class RecipeCommandValidator : ICommandValidator<DefineRecipeVersionCommand>,
    ICommandValidator<ApproveRecipeVersionCommand>, ICommandValidator<ApplyRecipeCommand>
{
    public IEnumerable<ValidationFailure> Validate(DefineRecipeVersionCommand command) =>
        Key(command.RecipeId, command.Version).Concat(Text(command.ProductCode, "ProductCode", 100))
            .Concat(Text(command.StepCode, "StepCode", 20)).Concat(Text(command.EquipmentClass, "EquipmentClass", 50))
            .Concat(command.Parameters.IsDefaultOrEmpty || command.Parameters.Length > 200
                ? [new ValidationFailure("Parameters", "Recipe cần 1–200 tham số.")] : []);

    public IEnumerable<ValidationFailure> Validate(ApproveRecipeVersionCommand command) =>
        Key(command.RecipeId, command.Version).Concat(command.SignatureIds.IsDefaultOrEmpty || command.EffectiveFrom == default
            ? [new ValidationFailure("SignatureIds", "Cần chữ ký và thời điểm hiệu lực.")] : []);

    public IEnumerable<ValidationFailure> Validate(ApplyRecipeCommand command) =>
        Text(command.EquipmentPath, "EquipmentPath", 200).Concat(Text(command.EquipmentClass, "EquipmentClass", 50))
            .Concat(Text(command.ProductCode, "ProductCode", 100)).Concat(Text(command.StepCode, "StepCode", 20))
            .Concat(Text(command.OperationRunId, "OperationRunId", 100));

    private static IEnumerable<ValidationFailure> Key(string? id, int version) =>
        Text(id, "RecipeId", 50).Concat(version is >= 1 and <= 100_000 ? [] : [new ValidationFailure("Version", "Version phải từ 1.")]);

    private static IEnumerable<ValidationFailure> Text(string? value, string field, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum
            ? [new ValidationFailure(field, $"{field} phải có 1–{maximum} ký tự.")] : [];
}
