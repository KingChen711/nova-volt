using System.Collections.Concurrent;
using System.Collections.Frozen;
using Nvm.FactoryModel.Entities;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Storage;

/// <summary>Trả lời <see cref="IEquipmentDirectory"/> dựa trên revision mà mỗi plant đang chạy.</summary>
/// <remarks>
/// <para>
/// Lượt tra cứu đi qua <see cref="IActiveFactoryModel"/> thay vì qua catalog, và đó chính là mục đích
/// của nó: một message đến từ NV1 phải được đọc dựa trên cái cây mà NV1 đang chạy, ngay cả khi DE1
/// vẫn còn ở một revision cũ hơn. Nếu thay vào đó hỏi catalog để lấy document mới nhất thì sẽ resolve
/// nhầm các device mà plant gửi message đó còn chưa được rollout tới.
/// </para>
/// <para>
/// Một index được build cho từng cặp plant và revision rồi được giữ lại. <see cref="FactorySite"/>
/// mang theo một cái cây và không có index, nên nếu không có bước này thì mọi lượt tra cứu sẽ phải đi
/// bộ qua nó — bốn mươi node ở hiện tại, và một lần cho mỗi message ở tốc độ năm nghìn message một
/// giây. Được key theo cả revision lẫn plant để một lần activation không làm mất hiệu lực bất cứ thứ
/// gì: nó chỉ đơn giản sinh ra một key mà chưa gì từng cache tới.
/// </para>
/// </remarks>
public sealed class FactoryModelEquipmentDirectory : IEquipmentDirectory
{
    private readonly IActiveFactoryModel _active;
    private readonly ConcurrentDictionary<(string SiteId, int Revision), FrozenDictionary<EquipmentPath, FactoryNode>> _indexes = new();

    /// <summary>Tạo directory dựa trên những gì mỗi plant hiện đang chạy.</summary>
    /// <param name="active">Kho lưu activation.</param>
    public FactoryModelEquipmentDirectory(IActiveFactoryModel active)
    {
        ArgumentNullException.ThrowIfNull(active);

        _active = active;
    }

    /// <inheritdoc />
    public bool Contains(EquipmentPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return IndexFor(path.SiteId)?.ContainsKey(path) ?? false;
    }

    /// <inheritdoc />
    public EquipmentPath? FindDevice(EquipmentPath line, string deviceCode)
    {
        ArgumentNullException.ThrowIfNull(line);

        var index = IndexFor(line.SiteId);

        if (index is null || string.IsNullOrEmpty(deviceCode))
        {
            return null;
        }

        // Xây bằng TryParse thay vì Append: code này đến từ một message trên dây, nên nó có thể là bất
        // cứ gì, còn Append lại trả lời một segment sai định dạng bằng một exception. Một device tự
        // xưng bằng một cái tên bất khả thi là một message cần bị từ chối, không phải một lỗi cần được
        // ném ra từ một lượt tra cứu directory.
        if (EquipmentPath.TryParse($"{line.Value}{EquipmentPath.Separator}{deviceCode}", out var direct)
            && index.ContainsKey(direct))
        {
            return direct;
        }

        if (!index.TryGetValue(line, out var lineNode))
        {
            return null;
        }

        foreach (var workCell in lineNode.Children)
        {
            if (EquipmentPath.TryParse($"{workCell.Path.Value}{EquipmentPath.Separator}{deviceCode}", out var nested)
                && index.ContainsKey(nested))
            {
                return nested;
            }
        }

        return null;
    }

    private FrozenDictionary<EquipmentPath, FactoryNode>? IndexFor(string? siteId)
    {
        // Trả null cho một path ở cấp enterprise, vì nó không thể nêu tên một device và cũng không thể
        // nêu tên một plant để resolve dựa trên đó. K3 tự nhiên mà có từ đây: một topic nhắc tới một
        // plant chưa ai activate sẽ không có index nào cả, nên không gì bên trong nó resolve được.
        if (siteId is null)
        {
            return null;
        }

        var current = _active.Current(siteId);

        return current is null
            ? null
            : _indexes.GetOrAdd(
                (siteId, current.Revision),
                static (_, site) => site.Root.Descend().ToFrozenDictionary(node => node.Path),
                current.Site);
    }
}
