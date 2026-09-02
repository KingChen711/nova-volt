using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;

namespace Nvm.EdgeGateway;

/// <summary>Xây dựng view chỉ-đọc (read-only) của gateway về factory model đã được publish.</summary>
internal static class GatewayEquipmentDirectory
{
    /// <summary>Tải một revision và kích hoạt các cây site của nó trong tiến trình này.</summary>
    internal static IEquipmentDirectory Load(string seedDirectory, int? requestedRevision) =>
        SeededEquipmentDirectory.Load(seedDirectory, requestedRevision);
}
