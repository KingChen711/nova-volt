using Nvm.FactoryModel.Seeding;

namespace Nvm.Host.Infrastructure;

/// <summary>Tìm ra <c>deploy/seed</c> mà không ai phải gõ tay một đường dẫn.</summary>
/// <remarks>
/// <para>
/// Model được đọc từ đĩa thay vì embed vào assembly (M1/C07), để một thay đổi plant là một file được
/// thêm vào chứ không phải một lần rebuild. Điều đó để lại câu hỏi: các file đó nằm ở đâu, và câu trả
/// lời khác nhau giữa chạy <c>dotnet run</c> từ project directory và chạy một binary xuất phát từ
/// <c>artifacts/bin</c>.
/// </para>
/// <para>
/// Vì vậy dùng đúng cách đi ngược lên mà <c>DotEnvLoader</c> đã dùng: bắt đầu từ nơi process đang chạy
/// và leo lên tới khi thấy repository. Một quy tắc, một hành vi, và không có gì phải cấu hình cho tới
/// khi có một deployment thật — lúc đó <c>NVM_SEED_DIR</c> tiếp quản và việc đi ngược lên không bao
/// giờ chạy nữa.
/// </para>
/// <para>
/// Một directory chứ không phải một file, vì một revision là một document và một plant có nhiều hơn
/// một revision (<see cref="Nvm.FactoryModel.Storage.IFactoryModelCatalog"/>). Việc đi ngược lên tìm
/// một directory thực sự chứa một revision document, chứ không chỉ là một directory tên <c>seed</c>:
/// nếu không, một directory rỗng nằm sâu hơn trong cây sẽ che khuất directory thật, và process sẽ chết
/// trong khi báo sai nguyên nhân.
/// </para>
/// </remarks>
internal static class SeedDirectoryLocator
{
    /// <summary>Biến môi trường nêu thẳng tên directory.</summary>
    public const string PathVariable = "NVM_SEED_DIR";

    private const string RelativePath = "deploy/seed";

    /// <summary>Tìm seed directory, bắt đầu từ nơi process được khởi chạy.</summary>
    /// <param name="contentRootPath">Directory để bắt đầu tìm ngược lên.</param>
    /// <exception cref="DirectoryNotFoundException">Không có directory tổ tiên nào chứa các seed document.</exception>
    public static string Locate(string contentRootPath)
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured)
        {
            return configured;
        }

        var directory = new DirectoryInfo(contentRootPath);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RelativePath);

            if (HoldsARevision(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"No '{RelativePath}' holding '{FactoryModelSeed.FileNameFor(1)}' found in "
            + $"'{contentRootPath}' or any directory above it. "
            + $"Set {PathVariable} to name the directory directly.");
    }

    private static bool HoldsARevision(string candidate) =>
        Directory.Exists(candidate)
        && Directory
            .EnumerateFiles(
                candidate,
                FactoryModelSeed.FileNamePrefix + "*" + FactoryModelSeed.FileNameSuffix)
            .Any();
}
