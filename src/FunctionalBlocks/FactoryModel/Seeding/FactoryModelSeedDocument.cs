using System.Text.Json.Serialization;

namespace Nvm.FactoryModel.Seeding;

/// <summary>Hình dạng của một document <c>deploy/seed/factory-model.r*.json</c> khi nó nằm trên đĩa.</summary>
/// <param name="Revision">File này mô tả revision nào của plant. Bắt đầu từ 1.</param>
/// <param name="GeneratedAt">Khi nào file được tạo ra, kèm offset rõ ràng.</param>
/// <param name="Enterprise">Gốc của cái cây.</param>
/// <remarks>
/// Cố ý tách riêng khỏi bất cứ thứ gì đi trên bus. Đây là một định dạng <i>import</i> — hiện tại là
/// một file được duy trì thủ công, tới M11 sẽ là bất cứ thứ gì một hệ thống kỹ thuật xuất ra — và nó
/// thay đổi vì những lý do hoàn toàn khác với một wire contract. Dùng chung một cấu hình serializer
/// giữa hai bên sẽ trói buộc những lý do đó lại với nhau.
/// </remarks>
internal sealed record FactoryModelSeedDocument(
    int Revision,
    DateTimeOffset GeneratedAt,
    FactoryNodeSeed Enterprise);

/// <summary>Một node trong seed file, ở bất kỳ cấp nào.</summary>
/// <param name="Code">Mã code riêng của node, viết hoa.</param>
/// <param name="Name">Tên hiển thị.</param>
/// <param name="TimeZoneId">
/// Time zone theo IANA. Chỉ xuất hiện trên một site, và bắt buộc phải có ở đó.
/// </param>
/// <param name="Children">Các node ở cấp thấp hơn một bậc, nếu có.</param>
/// <remarks>
/// Một hình dạng duy nhất cho mọi cấp, thay vì năm hình dạng được đặt tên riêng. Cấp độ không được ghi
/// ra ở bất cứ đâu trong file: nó được suy ra từ độ sâu node đang nằm, hệt như cách
/// <see cref="Kernel.Identity.EquipmentPath"/> làm. Đặt tên cho các cấp trong file sẽ tạo ra một nơi
/// thứ hai để nêu ra chúng, và vì vậy là một nơi để chúng bất đồng với nhau.
/// </remarks>
internal sealed record FactoryNodeSeed(
    string Code,
    string Name,
    string? TimeZoneId = null,
    IReadOnlyList<FactoryNodeSeed>? Children = null);

/// <summary>Cấu hình serializer cho seed file, và không gì khác.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FactoryModelSeedDocument))]
internal sealed partial class FactoryModelSeedJsonContext : JsonSerializerContext;
