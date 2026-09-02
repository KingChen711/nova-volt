using System.Globalization;
using System.Text.Json;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Seeding;

/// <summary>Đọc các document của factory model và biến chúng thành các snapshot đã validate.</summary>
/// <remarks>
/// <para>
/// Đọc từ đĩa thay vì embed vào assembly, để plant có thể được sửa lại mà không cần rebuild — và để
/// file luôn là thứ có thể đưa cho một kỹ sư xem, chỉnh sửa và diff trong một review.
/// </para>
/// <para>
/// <b>Một file cho mỗi revision</b>, đặt tên <c>factory-model.r2.json</c>. Một revision là một
/// document và document thì không bị sửa tại chỗ (<see cref="IFactoryModelCatalog"/>), nên một
/// revision mới là một file mới đứng cạnh file cũ chứ không phải một thay đổi lên nó. Directory chính
/// là cả cái kệ sách.
/// </para>
/// </remarks>
public static class FactoryModelSeed
{
    /// <summary>Mọi tên file revision đều bắt đầu bằng gì.</summary>
    public const string FileNamePrefix = "factory-model.r";

    /// <summary>Mọi tên file revision đều kết thúc bằng gì.</summary>
    public const string FileNameSuffix = ".json";

    /// <summary>Tên file mà một revision cho trước được kỳ vọng phải có.</summary>
    /// <param name="revision">Số revision, ít nhất là 1.</param>
    public static string FileNameFor(int revision)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);

        return FileNamePrefix + revision.ToString(CultureInfo.InvariantCulture) + FileNameSuffix;
    }

    /// <summary>Đọc mọi revision document trong một directory.</summary>
    /// <param name="directoryPath">Directory chứa các file <c>factory-model.r*.json</c>.</param>
    /// <exception cref="DirectoryNotFoundException">Directory không tồn tại.</exception>
    /// <exception cref="FactoryModelSeedException">
    /// Directory không chứa document nào, hoặc một trong số chúng không mô tả một plant hợp lệ, hoặc
    /// tên file và revision bên trong nó không khớp nhau.
    /// </exception>
    /// <remarks>
    /// Mọi vấn đề ở đây đều gây fatal lúc startup, và không file nào bị bỏ qua trong im lặng. Một
    /// document bị bỏ qua âm thầm là một rollout đã publish nhưng biến mất, và không ai biết được cho
    /// tới ngày một operator activate một revision mà catalog chưa bao giờ load.
    /// </remarks>
    public static InMemoryFactoryModelCatalog LoadCatalog(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Factory model seed directory not found: '{directoryPath}'. "
                + $"It must hold at least '{FileNameFor(1)}'.");
        }

        var files = Directory.GetFiles(directoryPath, FileNamePrefix + "*" + FileNameSuffix);

        if (files.Length == 0)
        {
            throw new FactoryModelSeedException(
                $"No factory model document in '{directoryPath}'. "
                + $"Revision files are named '{FileNameFor(1)}'.");
        }

        var snapshots = new List<FactoryModelSnapshot>(files.Length);

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var revisionText = name[FileNamePrefix.Length..^FileNameSuffix.Length];

            if (!int.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            {
                throw new FactoryModelSeedException(
                    $"'{name}' is not a revision file name. "
                    + $"Expected '{FileNamePrefix}<number>{FileNameSuffix}'.");
            }

            var snapshot = Load(file);

            // Tên file và nội dung bên trong phải khớp nhau. Copy r2 thành r3 rồi quên đổi con số bên
            // trong là lỗi dễ mắc nhất ở đây, và nó sẽ đưa cái cây cũ vào hiệu lực dưới một số revision
            // mới — một thay đổi mà ai cũng tin là đã xảy ra trong khi thực chất không hề.
            if (snapshot.Revision != revision)
            {
                throw new FactoryModelSeedException(
                    $"'{name}' holds revision {snapshot.Revision}. "
                    + "The file name and the document must name the same revision.");
            }

            snapshots.Add(snapshot);
        }

        return new InMemoryFactoryModelCatalog(snapshots);
    }

    /// <summary>Đọc và validate một revision document duy nhất.</summary>
    /// <param name="filePath">Đường dẫn đầy đủ tới file JSON.</param>
    /// <exception cref="FileNotFoundException">File không tồn tại.</exception>
    /// <exception cref="FactoryModelSeedException">File tồn tại nhưng không mô tả một plant hợp lệ.</exception>
    public static FactoryModelSnapshot Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"Factory model document not found. Expected a file named '{FileNameFor(1)}' or "
                + "another revision at this path.",
                filePath);
        }

        return Parse(File.ReadAllText(filePath));
    }

    /// <summary>Validate nội dung seed đã được đọc từ trước.</summary>
    /// <param name="json">Nội dung của file.</param>
    /// <exception cref="FactoryModelSeedException">Nội dung không mô tả một plant hợp lệ.</exception>
    public static FactoryModelSnapshot Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        FactoryModelSeedDocument? document;

        try
        {
            document = JsonSerializer.Deserialize(json, FactoryModelSeedJsonContext.Default.FactoryModelSeedDocument);
        }
        catch (JsonException failure)
        {
            throw new FactoryModelSeedException("The factory model seed is not valid JSON.", failure);
        }

        if (document is null)
        {
            throw new FactoryModelSeedException("The factory model seed is empty.");
        }

        // Revision 0 chính là hình dạng của một số integer chưa được khởi tạo, và một revision không
        // bao giờ dịch chuyển thì không thể phân biệt được với một plant chưa bao giờ thay đổi.
        if (document.Revision < 1)
        {
            throw new FactoryModelSeedException(
                $"Revision must be at least 1, found {document.Revision}.");
        }

        if (!EquipmentPath.TryParse(document.Enterprise.Code, out var rootPath))
        {
            throw new FactoryModelSeedException(
                $"'{document.Enterprise.Code}' is not a valid enterprise code. Codes are upper case.");
        }

        var root = BuildNode(document.Enterprise, rootPath);

        return new FactoryModelSnapshot(document.Revision, document.GeneratedAt, root, CollectSites(root, document));
    }

    private static FactoryNode BuildNode(FactoryNodeSeed seed, EquipmentPath path)
    {
        // Time zone chỉ thuộc về một plant và không thuộc về gì khác. Thấy nó xuất hiện trên một work
        // cell nghĩa là hoặc file bị sai, hoặc ai đó sắp bắt đầu đọc nó từ sai cấp.
        var isSite = path.Kind == FactoryNodeKind.Site;

        if (isSite && string.IsNullOrWhiteSpace(seed.TimeZoneId))
        {
            throw new FactoryModelSeedException($"Site '{path}' has no timeZoneId.");
        }

        if (!isSite && seed.TimeZoneId is not null)
        {
            throw new FactoryModelSeedException(
                $"'{path}' is a {path.Kind} and carries a timeZoneId. Only a {FactoryNodeKind.Site} has one.");
        }

        var children = new List<FactoryNode>();

        foreach (var child in seed.Children ?? [])
        {
            EquipmentPath childPath;

            try
            {
                childPath = path.Append(child.Code);
            }
            catch (Exception failure) when (failure is FormatException or InvalidOperationException)
            {
                throw new FactoryModelSeedException(
                    $"'{child.Code}' cannot sit under '{path}': {failure.Message}",
                    failure);
            }

            children.Add(BuildNode(child, childPath));
        }

        try
        {
            return FactoryNode.Create(path, seed.Name, children);
        }
        catch (ArgumentException failure)
        {
            throw new FactoryModelSeedException($"'{path}' is not a valid node: {failure.Message}", failure);
        }
    }

    private static List<FactorySite> CollectSites(FactoryNode root, FactoryModelSeedDocument document)
    {
        var timeZones = (document.Enterprise.Children ?? [])
            .ToDictionary(site => site.Code, site => site.TimeZoneId!, StringComparer.Ordinal);

        return [.. root.Children.Select(node =>
            new FactorySite(node.Code, node.Name, timeZones[node.Code], node))];
    }
}
