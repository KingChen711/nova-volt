using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nvm.Contracts.Events.Recipe;

namespace Nvm.Recipe.Entities;

public enum RecipeStatus { Draft, Active, Superseded }

/// <summary>
/// Một version của recipe cho khoá (EquipmentClass, ProductCode, StepCode). Bất biến sau khi được duyệt:
/// đổi tham số là tạo version mới (scope §6.8).
/// </summary>
public sealed record RecipeVersion(string RecipeId, int Version, string ProductCode, string StepCode, string EquipmentClass,
    ImmutableArray<RecipeParameter> Parameters, RecipeStatus Status = RecipeStatus.Draft, DateTimeOffset? EffectiveFrom = null,
    DateTimeOffset? EffectiveTo = null)
{
    public string SubjectId => $"{RecipeId}:v{Version.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Hash nội dung tất định (không gồm trạng thái/hiệu lực): thứ được ký và được ghi khi apply.</summary>
    public string ContentSha256()
    {
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture,
            $"{RecipeId}|{Version}|{ProductCode}|{StepCode}|{EquipmentClass}");
        foreach (var p in Parameters.OrderBy(p => p.Name, StringComparer.Ordinal))
        { text.Append(CultureInfo.InvariantCulture, $"|{p.Name}:{p.Target}:{p.Min}:{p.Max}:{p.UnitOfMeasure}"); }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Parameters.IsDefaultOrEmpty)
        { problems.Add("Recipe phải có ít nhất một tham số."); }
        else
        {
            if (Parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != Parameters.Length)
            { problems.Add("Tên tham số bị trùng."); }
            if (Parameters.Any(p => p.Min > p.Target || p.Target > p.Max))
            { problems.Add("Mỗi tham số phải có Min ≤ Target ≤ Max."); }
        }
        return problems;
    }
}

public static class RecipeReasonCodes
{
    public const string InvalidRecipe = "INVALID_RECIPE";
    public const string RecipeExists = "RECIPE_VERSION_EXISTS";
    public const string RecipeNotFound = "RECIPE_NOT_FOUND";
    public const string NotDraft = "RECIPE_NOT_DRAFT";
    public const string AlreadyActive = "ANOTHER_VERSION_ACTIVE";
    public const string EffectiveBeforeCurrent = "EFFECTIVE_BEFORE_CURRENT";
    public const string NoActiveRecipe = "NO_ACTIVE_RECIPE";
    public const string ClassMismatch = "EQUIPMENT_CLASS_MISMATCH";
}
