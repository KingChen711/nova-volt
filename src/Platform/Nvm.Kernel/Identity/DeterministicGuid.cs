using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Nvm.Kernel.Identity;

/// <summary>
/// Xây định danh RFC 4122 version 5: cùng namespace và cùng name luôn cho ra cùng một GUID.
/// </summary>
/// <remarks>
/// <para>
/// Số version trong một UUID gọi tên một thuật toán, không phải một thế hệ. Version 7 mới hơn và không
/// dùng được ở đây: nó trộn thêm một timestamp, nên gọi nó hai lần cho cùng một measurement cho ra hai
/// giá trị khác nhau. Chỉ version 3 và 5 được suy ra từ input của chúng, và version 5 hash bằng SHA-1
/// trong khi version 3 dùng MD5.
/// </para>
/// <para>
/// SHA-1 đã bị phá vỡ như một hàm hash mật mã và điều đó không quan trọng ở đây. Không có gì cần được
/// bảo vệ; hash chỉ trải đều các bit của input trên 128 bit. Không ai được lợi gì từ việc tạo ra hai
/// natural key va chạm nhau. Đây là lý do <c>CA5351</c> bị tắt trong <c>.editorconfig</c>, kèm lý do
/// được ghi ngay trên dòng tắt nó.
/// </para>
/// <para>
/// BCL cung cấp <see cref="Guid.CreateVersion7()"/> và không có version 5, nên đoạn này được viết tay —
/// xem docs/plans/M1-factory-model-bus.md §C04.1.
/// </para>
/// </remarks>
public static class DeterministicGuid
{
    /// <summary>DNS namespace do RFC 4122 định nghĩa, dùng làm gốc cho mọi namespace được suy ra.</summary>
    public static readonly Guid DnsNamespace = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    private const int HashLength = 20;
    private const int GuidLength = 16;
    private const int VersionByte = 6;
    private const int VariantByte = 8;

    /// <summary>Suy ra một GUID version 5 từ một namespace và một name.</summary>
    /// <param name="namespaceId">Namespace mà name được diễn giải bên trong.</param>
    /// <param name="name">Name, được hash dưới dạng UTF-8.</param>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification =
            "RFC 4122 defines version 5 as SHA-1 over namespace and name. The hash is a bit mixer here, "
            + "not a security control: nothing is authenticated, and an attacker gains nothing by finding "
            + "two natural keys that collide. Suppressed at this one method so CA5350 keeps guarding every "
            + "other use of SHA-1 in the repository.")]
    public static Guid CreateVersion5(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Big-endian có chủ đích. .NET đặt ba trường đầu của một Guid theo little-endian trong bộ nhớ,
        // nên thứ tự byte mặc định là một chi tiết của .NET chứ không phải wire format mà RFC 4122 mô
        // tả. Hash các byte little-endian vẫn sẽ deterministic, nhưng sẽ cho ra giá trị khác với mọi
        // implementation khác trên đời — và định danh này được thiết kế để bất kỳ ai audit dữ liệu cũng
        // tái tạo lại được, bằng bất kỳ ngôn ngữ nào họ dùng.
        Span<byte> namespaceBytes = stackalloc byte[GuidLength];
        namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _);

        var nameBytes = Encoding.UTF8.GetBytes(name);

        Span<byte> hash = stackalloc byte[HashLength];
        SHA1.HashData([.. namespaceBytes, .. nameBytes], hash);

        // Ghi đè bốn bit bằng version và hai bit bằng variant, đúng như RFC yêu cầu. Bỏ qua bước này
        // vẫn cho ra một giá trị 128-bit deterministic dedup hoàn toàn tốt — và nó không phải một UUID.
        // Thiệt hại chỉ lộ ra ở ranh giới, khi một cột uuid PostgreSQL hoặc công cụ của auditor từ chối
        // đọc thứ đã nằm sẵn trong store.
        hash[VersionByte] = (byte)((hash[VersionByte] & 0x0F) | 0x50);
        hash[VariantByte] = (byte)((hash[VariantByte] & 0x3F) | 0x80);

        return new Guid(hash[..GuidLength], bigEndian: true);
    }
}
