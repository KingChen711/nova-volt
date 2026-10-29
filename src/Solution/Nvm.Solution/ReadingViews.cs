using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Nvm.Foundation.Runtime;
using Nvm.Project;

namespace Nvm.Solution;

/// <summary>
/// Bước cài view của reading model, chạy riêng sau Update Database (<c>make views</c>). Không chạy bước này thì reading
/// function vẫn trả HTTP 200 nhưng không có dòng nào (lab L15).
/// </summary>
public static partial class ReadingViews
{
    /// <summary>Cài (hoặc thay) mọi view của team. Trả tên các view đã cài.</summary>
    public static IReadOnlyList<string> Install(string connectionString, ModelRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        foreach (var template in NvViews.Templates.Values)
        {
            // SQL là template trong code của team, tên bảng lấy từ registry; không có dữ liệu người dùng.
#pragma warning disable CA2100
            using var command = new SqlCommand(Render(template, registry), connection);
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        return [.. NvViews.Templates.Keys];
    }

    /// <summary>Thay <c>{{table:Lớp}}</c> và <c>{{id:Facet}}</c> bằng tên thật trong registry.</summary>
    public static string Render(string template, ModelRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Placeholder().Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            var entity = registry.Entities.SingleOrDefault(e => e.Type.Name == name);
            var facet = registry.Facets.SingleOrDefault(f => f.Type.Name == name);
            return match.Groups["kind"].Value switch
            {
                "table" when entity is not null => entity.TableName,
                "table" when facet is not null => registry.FacetTables.Single(t => t.Entity == facet.Entity).TableName,
                "id" when facet is not null => registry.FacetTables.Single(t => t.Entity == facet.Entity).EntityIdColumn,
                _ => throw new InvalidOperationException($"View template refers to unknown model '{name}'."),
            };
        });
    }

    [GeneratedRegex(@"\{\{(?<kind>table|id):(?<name>[A-Za-z0-9_]+)\}\}")]
    private static partial Regex Placeholder();
}
