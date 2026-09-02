using System.Globalization;
using System.Text;
using Nvm.Kernel.Identity;

namespace Nvm.Kernel.Commands;

/// <summary>
/// Giá trị trả lời câu hỏi "mình đã xử lý cái này chưa?".
/// </summary>
/// <remarks>
/// <para>
/// Thiết bị gửi lại khi không nhận được acknowledgement. Một gateway từng offline sẽ đẩy hết backlog
/// của nó. Bản thân bus là at-least-once. Vì vậy cùng một fact đến hai hoặc ba lần, và đó là vận hành
/// bình thường chứ không phải một lỗi (docs/scope.md §7.2).
/// </para>
/// <para>
/// Nên khoá này không bao giờ được sinh ra — nó <b>được suy ra từ chính fact đó</b>, qua một version 5
/// GUID trên natural key. Hai lần gửi của cùng một measurement cho ra cùng một giá trị, trên bất kỳ
/// máy nào, trong bất kỳ process nào, cách nhau ba ngày.
/// </para>
/// <para>
/// Deduplication xảy ra ở hai chỗ và cả hai đều dùng khoá này: ở ingestion, để loại một message thiết
/// bị bị lặp lại, và bên trong command pipeline, vì bus cũng có thể redeliver (AGENTS.md K7). Hai tầng
/// này chỉ ghép được với nhau nếu chúng cùng khoá trên một giá trị, đó là lý do <c>id</c> CloudEvents
/// của một event do một command tạo ra phải bằng đúng khoá của command đó.
/// </para>
/// </remarks>
public sealed record IdempotencyKey
{
    /// <summary>
    /// Namespace gốc cho mọi định danh deterministic trong hệ thống này.
    /// </summary>
    /// <remarks>
    /// Được suy ra chứ không phải tự đặt, để bất kỳ ai cũng tính lại được: version 5 của DNS namespace
    /// RFC 4122 trên <c>novavolt.example</c>. Một GUID ngẫu nhiên hard-code cũng chạy tốt như vậy
    /// nhưng sẽ không thể kiểm chứng được.
    /// </remarks>
    public static readonly Guid NovaVoltNamespace =
        DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "novavolt.example");

    private const char PartSeparator = '|';
    private const char LengthSeparator = ':';

    private IdempotencyKey(Guid value) => Value = value;

    /// <summary>Chính khoá đó.</summary>
    public Guid Value { get; }

    /// <summary>Bọc lại một khoá đã được suy ra ở nơi khác, ví dụ đọc lại từ một message.</summary>
    /// <exception cref="ArgumentException">Giá trị là <see cref="Guid.Empty"/>.</exception>
    public static IdempotencyKey From(Guid value)
    {
        // Một khoá toàn số 0 chính là hình dạng của một lần quên gán giá trị, và nó sẽ dedup mọi
        // command bị quên đó thành một — âm thầm, và chỉ lộ ra khi có tải.
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key cannot be empty.", nameof(value));
        }

        return new IdempotencyKey(value);
    }

    /// <summary>Suy ra một khoá từ các phần của natural key, trong namespace gốc của hệ thống.</summary>
    /// <param name="parts">
    /// Các trường định danh fact đó, theo một thứ tự cố định — với một measurement:
    /// site, equipment, unit, step code, device timestamp, signal code.
    /// </param>
    public static IdempotencyKey FromNaturalKey(params string[] parts) =>
        FromNaturalKey(NovaVoltNamespace, parts);

    /// <summary>Suy ra một khoá từ các phần của natural key, trong một namespace tường minh.</summary>
    /// <param name="namespaceId">Namespace để suy ra khoá bên trong đó.</param>
    /// <param name="parts">Các trường định danh fact đó, theo một thứ tự cố định.</param>
    /// <exception cref="ArgumentException">Không có phần nào được truyền vào, hoặc một phần là null.</exception>
    public static IdempotencyKey FromNaturalKey(Guid namespaceId, params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Length == 0)
        {
            throw new ArgumentException("A natural key needs at least one part.", nameof(parts));
        }

        return new IdempotencyKey(DeterministicGuid.CreateVersion5(namespaceId, Encode(parts)));
    }

    /// <summary>Trả về khoá dưới dạng GUID chuẩn.</summary>
    public override string ToString() => Value.ToString();

    /// <summary>
    /// Nối các phần lại sao cho một chuỗi chỉ có thể đến từ đúng một tuple.
    /// </summary>
    /// <remarks>
    /// Mỗi phần được viết dưới dạng <c>length:value|</c>. <c>string.Join('|', parts)</c> trần trụi
    /// trông có vẻ tương đương nhưng không phải: <c>["a|b", "c"]</c> và <c>["a", "b|c"]</c> đều gộp
    /// phẳng thành <c>"a|b|c"</c>, nên hai fact khác nhau suy ra cùng một khoá và một trong hai bị biến
    /// mất ở bước deduplication. Mã lot của nhà cung cấp là free text từ hệ thống của bên khác, nên một
    /// separator xuất hiện bên trong một giá trị chỉ là vấn đề thời gian.
    /// <para>
    /// Thêm tiền tố độ dài khiến phép mã hoá này trở thành injective, đây chính là tính chất mà toàn bộ
    /// cơ chế này dựa vào: input khác nhau, khoá khác nhau. Luôn luôn.
    /// </para>
    /// </remarks>
    private static string Encode(string[] parts)
    {
        var builder = new StringBuilder();

        foreach (var part in parts)
        {
            if (part is null)
            {
                throw new ArgumentException(
                    "A natural key part cannot be null. Pass an empty string when a field is genuinely absent.",
                    nameof(parts));
            }

            builder
                .Append(part.Length.ToString(CultureInfo.InvariantCulture))
                .Append(LengthSeparator)
                .Append(part)
                .Append(PartSeparator);
        }

        return builder.ToString();
    }
}
