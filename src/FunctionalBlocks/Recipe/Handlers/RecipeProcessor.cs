using System.Collections.Immutable;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Recipe;
using Nvm.Contracts.Ports;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Recipe.Commands;
using Nvm.Recipe.Entities;

namespace Nvm.Recipe.Handlers;

/// <summary>Recipe version đã lưu, kèm người soạn và stream version.</summary>
public sealed record StoredRecipe(RecipeVersion Recipe, string AuthoredBy);

/// <summary>Recipe trong transaction của command. DB là nơi chặn hai version Active cho cùng khoá.</summary>
public interface IRecipeStore
{
    Task<StoredRecipe?> LoadForUpdateAsync(string siteId, string recipeId, int version, CancellationToken cancellationToken);

    Task SaveDraftAsync(string siteId, RecipeVersion recipe, string author, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Version đang Active cho khoá, khoá dòng tới commit.</summary>
    Task<RecipeVersion?> ActiveForUpdateAsync(string siteId, string equipmentClass, string productCode, string stepCode,
        CancellationToken cancellationToken);

    /// <summary>Kết thúc version cũ tại <paramref name="effectiveFrom"/> và cho version mới Active; false nếu DB từ chối.</summary>
    Task<bool> ActivateAsync(string siteId, RecipeVersion recipe, RecipeVersion? previous, DateTimeOffset effectiveFrom,
        string approver, string sha256, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Version có hiệu lực tại thời điểm <paramref name="at"/> (theo khoảng [EffectiveFrom, EffectiveTo)).</summary>
    Task<RecipeVersion?> EffectiveAtAsync(string siteId, string equipmentClass, string productCode, string stepCode,
        DateTimeOffset at, CancellationToken cancellationToken);

    Task RecordApplicationAsync(string siteId, RecipeVersionApplied applied, CancellationToken cancellationToken);
}

public sealed class RecipeProcessor(IEventStore events, IRecipeStore recipes, ISignatureVerifier signatures, TimeProvider clock) :
    ICommandHandler<DefineRecipeVersionCommand, DomainCommandResult>,
    ICommandHandler<ApproveRecipeVersionCommand, DomainCommandResult>,
    ICommandHandler<ApplyRecipeCommand, DomainCommandResult>
{
    /// <summary>Vai trò phải ký khi duyệt recipe: chất lượng và sản xuất, không ai là người soạn.</summary>
    public static readonly string[] ApprovalRoles = ["QaManager", "ProductionManager"];

    public async Task<DomainCommandResult> HandleAsync(DefineRecipeVersionCommand command, CancellationToken cancellationToken)
    {
        var recipe = new RecipeVersion(command.RecipeId, command.Version, command.ProductCode, command.StepCode,
            command.EquipmentClass, command.Parameters);
        if (recipe.Problems() is { Count: > 0 } problems)
        { return DomainCommandResult.Reject(RecipeReasonCodes.InvalidRecipe, string.Join(" ", problems)); }
        if (await recipes.LoadForUpdateAsync(command.SiteId, command.RecipeId, command.Version, cancellationToken)
                .ConfigureAwait(false) is not null)
        { return DomainCommandResult.Reject(RecipeReasonCodes.RecipeExists, "Version này đã tồn tại."); }
        await recipes.SaveDraftAsync(command.SiteId, recipe, command.ActorId, clock.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, null, null, recipe.ContentSha256());
    }

    public async Task<DomainCommandResult> HandleAsync(ApproveRecipeVersionCommand command, CancellationToken cancellationToken)
    {
        var stored = await recipes.LoadForUpdateAsync(command.SiteId, command.RecipeId, command.Version, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null)
        { return DomainCommandResult.Reject(RecipeReasonCodes.RecipeNotFound, "Không tìm thấy recipe."); }
        if (stored.Recipe.Status != RecipeStatus.Draft)
        { return DomainCommandResult.Reject(RecipeReasonCodes.NotDraft, "Recipe này đã được duyệt."); }
        var recipe = stored.Recipe;
        var sha = recipe.ContentSha256();
        if (await signatures.VerifyAsync(command.SiteId, command.SignatureIds, "RecipeVersion", recipe.SubjectId, sha,
                ApprovalRoles, stored.AuthoredBy, cancellationToken).ConfigureAwait(false) is { } reason)
        { return DomainCommandResult.Reject(reason, "Chữ ký duyệt recipe không đủ hoặc không hợp lệ."); }
        var previous = await recipes.ActiveForUpdateAsync(command.SiteId, recipe.EquipmentClass, recipe.ProductCode,
            recipe.StepCode, cancellationToken).ConfigureAwait(false);
        if (previous?.EffectiveFrom is { } from && command.EffectiveFrom <= from)
        { return DomainCommandResult.Reject(RecipeReasonCodes.EffectiveBeforeCurrent, "Hiệu lực mới phải sau version đang dùng."); }
        var now = clock.GetUtcNow();
        if (!await recipes.ActivateAsync(command.SiteId, recipe, previous, command.EffectiveFrom, command.ActorId, sha, now,
                cancellationToken).ConfigureAwait(false))
        { return DomainCommandResult.Reject(RecipeReasonCodes.AlreadyActive, "Đã có version khác đang active."); }
        var fact = new RecipeVersionApproved(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            recipe.RecipeId, recipe.Version, recipe.ProductCode, recipe.StepCode, recipe.EquipmentClass, recipe.Parameters,
            command.EffectiveFrom, sha, command.SignatureIds, previous?.Version, command.ActorId);
        var version = await AppendAsync(command.SiteId, $"recipe:{recipe.RecipeId}:v{recipe.Version}", "recipe-version", 0,
            fact, now, cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(ApplyRecipeCommand command, CancellationToken cancellationToken)
    {
        var recipe = await recipes.EffectiveAtAsync(command.SiteId, command.EquipmentClass, command.ProductCode,
            command.StepCode, command.OccurredAt, cancellationToken).ConfigureAwait(false);
        if (recipe is null)
        { return DomainCommandResult.Reject(RecipeReasonCodes.NoActiveRecipe, "Không có recipe hiệu lực cho máy/sản phẩm/bước này."); }
        var now = clock.GetUtcNow();
        var fact = new RecipeVersionApplied(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.EquipmentPath, command.EquipmentClass, command.ProductCode, command.StepCode, recipe.RecipeId,
            recipe.Version, recipe.ContentSha256(), command.OperationRunId, command.LotId, command.ActorId);
        var stream = "equipment-recipe:" + command.EquipmentPath;
        var current = await events.ReadStreamAsync(command.SiteId, stream, cancellationToken).ConfigureAwait(false);
        var version = await AppendAsync(command.SiteId, stream, "equipment-recipe", current?.Version ?? 0, fact, now,
            cancellationToken).ConfigureAwait(false);
        await recipes.RecordApplicationAsync(command.SiteId, fact, cancellationToken).ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, fact.EventId, version,
            $"{recipe.RecipeId} v{recipe.Version}");
    }

    private Task<long> AppendAsync(string siteId, string stream, string type, long expected, IDomainEvent fact,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        events.AppendAsync(siteId, stream, type, expected, ImmutableArray.Create(
            DomainEventRecord.Create(fact, "urn:recipe:" + stream, $"{siteId}:{stream}", now)), cancellationToken);
}
