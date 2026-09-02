using System.Collections.Immutable;
using Nvm.FactoryModel.Entities;
using Nvm.Kernel.Identity;

namespace Nvm.Simulator;

/// <summary>Đọc danh sách channel từ nhà máy thay vì tự bịa ra.</summary>
/// <remarks>
/// Simulator không được tự do bịa ra thiết bị. Một topic đặt tên một device mà nhà máy không có sẽ bị
/// ingestion (K3) từ chối, và một run mà mọi message của nó đều bị từ chối thì chẳng đo được gì —
/// thất bại đó sẽ trông như một pipeline bị hỏng chứ không phải một danh sách channel bịa đặt.
/// </remarks>
public static class FormationChannels
{
    /// <summary>Mọi channel bên dưới một line, theo thứ tự path.</summary>
    /// <param name="model">Revision mà nhà máy đang chạy.</param>
    /// <param name="linePath">Line cần tìm bên dưới.</param>
    /// <exception cref="InvalidOperationException">Revision này không có channel nào ở đó.</exception>
    /// <remarks>
    /// Chỉ ở cấp equipment. Một work cell không có gì bên dưới nó — <c>FORM-02</c> trong seed, một
    /// cycler chưa được mô hình hóa channel — là một machine, không phải một charging channel, và mô
    /// phỏng nó như một channel sẽ đặt formation reading lên một device không hề tồn tại.
    /// </remarks>
    public static ImmutableArray<EquipmentPath> Under(FactoryModelSnapshot model, EquipmentPath linePath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(linePath);

        var prefix = linePath.Value + EquipmentPath.Separator;

        var channels = model.Paths
            .Where(path => path.Kind == FactoryNodeKind.Equipment
                && path.Value.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(path => path.Value, StringComparer.Ordinal)
            .ToImmutableArray();

        return channels.IsEmpty
            ? throw new InvalidOperationException(
                $"Revision {model.Revision} has no equipment under '{linePath.Value}'. Either the line "
                + "is wrong or this revision does not model its channels yet.")
            : channels;
    }
}
