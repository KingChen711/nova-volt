using System.Globalization;

namespace Nvm.Hosting;

/// <summary>
/// Nạp file <c>.env</c> ở gốc repo vào process environment trong lúc phát triển.
/// </summary>
/// <remarks>
/// <para>
/// Cùng một file <c>.env</c> đã điều khiển docker-compose, nên đọc nó ở đây giữ được một nguồn sự
/// thật duy nhất cho port và credential. Phương án thay thế — copy sáu password vào
/// <c>appsettings.Development.json</c> — nghĩa là hai file được commit phải được giữ đồng bộ bằng
/// tay, và chúng sẽ trôi lệch nhau.
/// </para>
/// <para>
/// Chỉ dùng cho development. Ở mọi environment khác, process environment là nguồn có thẩm quyền và
/// loader này không làm gì cả, nên một app đã deploy không bao giờ có thể lấy nhầm file của một
/// developer.
/// </para>
/// <para>
/// Nó nằm trong Platform thay vì trong một App vì mọi deployable đều cần nó: ngay khoảnh khắc một
/// process thứ hai xuất hiện — bus probe worker — phương án còn lại là một bản sao thứ hai của cùng
/// bốn mươi dòng này, và hai bản sao của một parser file-format sẽ trôi lệch nhau ngay lần đầu tiên
/// một trong hai bản học được về quoted value.
/// </para>
/// </remarks>
public static class DotEnvLoader
{
    /// <summary>Nạp <c>.env</c> từ thư mục tổ tiên gần nhất có chứa một file như vậy.</summary>
    /// <param name="contentRootPath">Thư mục bắt đầu tìm ngược lên.</param>
    /// <returns>File đã được nạp, hoặc <see langword="null"/> khi không tìm thấy.</returns>
    public static string? Load(string contentRootPath)
    {
        var directory = new DirectoryInfo(contentRootPath);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                Apply(candidate);
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void Apply(string path)
    {
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            // Một biến đã tồn tại luôn thắng, nên `NVM_X=... dotnet run` vẫn override được file.
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>Đọc một biến bắt buộc, fail ồn ào thay vì tạo ra một connection string hỏng.</summary>
    public static string Required(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Environment variable '{key}' is not set. Copy .env.example to .env, or set it in the environment."));
}
