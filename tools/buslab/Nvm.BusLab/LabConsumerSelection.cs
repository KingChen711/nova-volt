namespace Nvm.BusLab;

/// <summary>Consumer nào của lab được lần chạy này khởi động.</summary>
/// <remarks>
/// <para>
/// Một switch duy nhất thay vì một flag cho mỗi consumer, vì các câu hỏi đang được đặt ra cần
/// consumer bị tắt <b>off</b> nhiều không kém gì bật on. Khẳng định rằng hai queue độc lập với nhau
/// chỉ được chứng minh bằng cách dừng một consumer rồi quan sát queue của nó đầy lên trong khi
/// consumer kia vẫn hoạt động — nếu cả hai luôn chạy, hai dòng log được giải thích như nhau bởi hai
/// queue riêng biệt lẫn bởi một queue bị đọc hai lần.
/// </para>
/// <para>
/// Consumer failing mặc định bị tắt vì một lý do khác: để bật, nó sẽ chuyển mọi measurement lab này
/// thấy vào một error queue mãi mãi, và một error queue lúc nào cũng có message trong đó là một error
/// queue không ai đọc.
/// </para>
/// </remarks>
public static class LabConsumerSelection
{
    /// <summary>Biến môi trường giữ một danh sách role phân tách bằng dấu phẩy.</summary>
    public const string Variable = "NVM_BUS_LAB_CONSUMERS";

    /// <summary>Consumer cache giữ giá trị mới nhất.</summary>
    public const string Cache = "cache";

    /// <summary>Consumer audit trail.</summary>
    public const string Audit = "audit";

    /// <summary>Consumer cố tình fail.</summary>
    public const string Failing = "failing";

    private static readonly string[] Default = [Cache, Audit];

    /// <summary>Một role có thuộc về lần chạy này hay không.</summary>
    public static bool Includes(string role) => Roles().Contains(role, StringComparer.OrdinalIgnoreCase);

    /// <summary>Các role mà lần chạy này được yêu cầu, dùng cho dòng log lúc khởi động.</summary>
    public static string Describe() => string.Join(", ", Roles());

    private static string[] Roles() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } configured
            ? configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : Default;
}
