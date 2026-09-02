using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Identity;

namespace Nvm.FactoryModel.Seeding;

/// <summary>Xây một view chỉ đọc của một revision đã publish của factory model.</summary>
/// <remarks>
/// Được dùng chung bởi mọi process cần trả lời "plant có thực sự sở hữu máy này không" mà không tự
/// giữ model: edge gateway đang resolve một Sparkplug topic, ingestion đang resolve một equipment
/// path từ một dòng CSV. Hai bản sao của loader này sẽ là hai nơi để quy tắc chọn revision lệch nhau,
/// và một gateway với một ingestion bất đồng về revision nào đang active thì sẽ chấp nhận và từ chối
/// những message khác nhau cho cùng một plant.
/// </remarks>
public static class SeededEquipmentDirectory
{
    /// <summary>Load một revision và activate các site tree của nó trong process này.</summary>
    /// <param name="seedDirectory">Directory chứa các document <c>factory-model.r*.json</c>.</param>
    /// <param name="requestedRevision">Revision cần load, hoặc null để lấy bản publish mới nhất.</param>
    /// <exception cref="InvalidOperationException">Catalog không có revision này.</exception>
    public static IEquipmentDirectory Load(string seedDirectory, int? requestedRevision)
    {
        var catalog = FactoryModelSeed.LoadCatalog(seedDirectory);
        var revision = requestedRevision ?? catalog.LatestRevision;
        var snapshot = catalog.Find(revision)
            ?? throw new InvalidOperationException(
                $"Revision {revision} is not on the shelf. The catalog holds {string.Join(", ", catalog.Revisions)}.");

        var active = new InMemoryActiveFactoryModel();

        foreach (FactorySite site in snapshot.Sites)
        {
            if (!active.TryActivate(new ActiveFactoryModelRevision(snapshot.Revision, site), expectedCurrentRevision: null))
            {
                throw new InvalidOperationException(
                    $"Site '{site.SiteId}' was activated twice while loading revision {snapshot.Revision}.");
            }
        }

        return new FactoryModelEquipmentDirectory(active);
    }
}
