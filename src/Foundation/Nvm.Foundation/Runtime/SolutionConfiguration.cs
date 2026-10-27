namespace Nvm.Foundation.Runtime;

/// <summary>Cấu hình của solution: app nào publish command nào, bật pre-check nào. Tương ứng Command Settings của nền thật.</summary>
public sealed class SolutionConfiguration
{
    public IList<AppConfiguration> Apps { get; } = [];

    /// <summary>Post-action tùy chọn đã tắt (theo tên lớp). Post-action bắt buộc thì không tắt được.</summary>
    public ISet<string> DisabledPostActions { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Response trả gì khi commit hỏng sau khi handler xong. Mặc định báo lỗi; biến thể kia dành cho lab L02.</summary>
    public CommitFailureReporting CommitFailureReporting { get; set; } = CommitFailureReporting.Failure;

    /// <summary>
    /// Command con báo lỗi mà handler cha vẫn trả thành công: có hủy cả transaction không. Chưa xác minh trên hệ thật, nên
    /// là cấu hình (scope §7); mặc định là không, kết quả do response của command gốc quyết định.
    /// </summary>
    public bool ChildFailureDoomsTransaction { get; set; }

    public AppConfiguration App(string name) =>
        Apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"App '{name}' is not part of the solution.");
}

public enum CommitFailureReporting
{
    /// <summary>Commit hỏng thì response thất bại.</summary>
    Failure = 1,

    /// <summary>Response giữ kết quả của handler (có thể <c>Succeeded</c>); lỗi commit chỉ thấy trong trace. Lab L02.</summary>
    HandlerResult = 2,
}

/// <summary>Một app: các command của FB nó publish và các pre-check nó bật.</summary>
public sealed class AppConfiguration(string name)
{
    public string Name { get; } = name;

    /// <summary>Command của FB mà app publish. Composite command của app thì tự thuộc app.</summary>
    public ISet<Type> Commands { get; } = new HashSet<Type>();

    /// <summary>Pre-check bật ở app này (theo tên lớp). Cùng command gọi qua app khác thì pre-check không chạy.</summary>
    public ISet<string> EnabledPreChecks { get; } = new HashSet<string>(StringComparer.Ordinal);
}
