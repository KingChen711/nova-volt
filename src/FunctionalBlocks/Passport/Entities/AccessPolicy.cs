using System.Collections.Frozen;

namespace Nvm.Passport.Entities;

/// <summary>Năm bên đọc passport (scope §7.7). Không phải role nội bộ: đến từ client scope của Keycloak.</summary>
public static class Audiences
{
    public const string Public = "public";
    public const string Consumer = "consumer";
    public const string Recycler = "recycler";
    public const string Repairer = "repairer";
    public const string Regulator = "regulator";

    public static readonly FrozenSet<string> All = new[] { Public, Consumer, Recycler, Repairer, Regulator }.ToFrozenSet();
}

/// <summary>Nhóm dữ liệu passport; mỗi trường thuộc đúng một nhóm.</summary>
public static class AccessClasses
{
    public const string Identity = "Identity";
    public const string Performance = "Performance";
    public const string Sustainability = "Sustainability";
    public const string Composition = "Composition";
    public const string Dismantling = "Dismantling";
    public const string Health = "Health";
    public const string Internal = "Internal";
}

/// <summary>
/// Ma trận nhóm dữ liệu × bên đọc của scope §7.7. Mặc định là từ chối: nhóm hay bên đọc không có trong ma trận thì không
/// thấy gì. <c>Health</c> với consumer chỉ mở cho chủ sở hữu của đúng pin đó.
/// </summary>
public static class AccessPolicy
{
    private static readonly FrozenDictionary<string, FrozenSet<string>> Matrix = new Dictionary<string, FrozenSet<string>>
    {
        [AccessClasses.Identity] = Audiences.All,
        [AccessClasses.Performance] = Audiences.All,
        [AccessClasses.Sustainability] = new[] { Audiences.Public, Audiences.Consumer, Audiences.Recycler, Audiences.Regulator }
            .ToFrozenSet(),
        [AccessClasses.Composition] = new[] { Audiences.Recycler, Audiences.Repairer, Audiences.Regulator }.ToFrozenSet(),
        [AccessClasses.Dismantling] = new[] { Audiences.Recycler, Audiences.Repairer, Audiences.Regulator }.ToFrozenSet(),
        [AccessClasses.Health] = new[] { Audiences.Consumer, Audiences.Recycler, Audiences.Repairer, Audiences.Regulator }
            .ToFrozenSet(),
        [AccessClasses.Internal] = new[] { Audiences.Regulator }.ToFrozenSet(),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static bool Allows(string audience, string? accessClass, bool isOwner)
    {
        if (accessClass is null || !Matrix.TryGetValue(accessClass, out var audiences) || !audiences.Contains(audience))
        { return false; }
        return accessClass != AccessClasses.Health || audience != Audiences.Consumer || isOwner;
    }
}
