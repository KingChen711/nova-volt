using System.Collections.Frozen;
using System.Collections.Immutable;
using Org.Eclipse.Tahu.Protobuf;

namespace Nvm.Sparkplug;

/// <summary>Những gì một birth đã khai báo: số nào ứng với metric nào, và kiểu gì.</summary>
/// <remarks>
/// <para>
/// Một máy formation có cả nghìn channel và mỗi channel sáu đến tám metric. Nếu viết đầy đủ
/// <c>Formation/Voltage</c> trong từng message thì sẽ tốn vài trăm byte tên cho bốn byte reading, năm
/// nghìn lần một giây, trên đường truyền hẹp nhất của nhà máy. Sparkplug giải quyết việc đó bằng
/// alias: birth nói <i>"metric 1 là Formation/Voltage, kiểu Float"</i> một lần duy nhất, rồi mọi
/// message sau đó chỉ gửi <c>1</c>.
/// </para>
/// <para>
/// Vậy nên bảng này không phải là cache. Nó là bản sao duy nhất chứa ý nghĩa của mọi message về sau,
/// và nó có phạm vi trong <b>một session của một node</b> — một node kết nối lại sẽ publish một birth
/// mới và được tự do gán số 1 cho một thứ khác. C11 là nơi hủy bảng này khi có <c>bdSeq</c> mới; cho
/// tới lúc đó nó được truyền vào một cách tường minh, giữ cho vòng đời có thể nhìn thấy được thay vì
/// giấu trong một static.
/// </para>
/// <para>
/// Datatype được giữ kèm theo tên nhưng cố tình không expose ra ngoài: đó là từ vựng của wire, không
/// phải của nhà máy, và để lộ type của <c>Org.Eclipse.Tahu.Protobuf</c> ra khỏi assembly này là phá
/// ranh giới mà ADR-026 yêu cầu giữ vững. Nó cần thiết ở bên trong vì trường <c>int_value</c> mang cả
/// <c>Int32</c> lẫn <c>UInt32</c>, và chỉ có khai báo mới nói được đó là loại nào.
/// </para>
/// </remarks>
public sealed class MetricAliasTable
{
    private readonly FrozenDictionary<ulong, MetricDefinition> _byAlias;

    private MetricAliasTable(FrozenDictionary<ulong, MetricDefinition> byAlias)
    {
        _byAlias = byAlias;
        Aliases = [.. byAlias.Keys.Order()];
    }

    /// <summary>Bảng trước khi có bất kỳ birth nào được thấy.</summary>
    /// <remarks>
    /// Không phải một null object âm thầm resolve được mọi thứ: mọi metric chỉ có alias mà decode dựa
    /// trên bảng này đều throw, đó là câu trả lời đúng cho tình huống "một data message đến trước cả
    /// birth của nó".
    /// </remarks>
    public static MetricAliasTable Empty { get; } = new(FrozenDictionary<ulong, MetricDefinition>.Empty);

    /// <summary>Các alias mà bảng này có thể resolve, theo thứ tự tăng dần.</summary>
    /// <remarks>
    /// Được tính trước thay vì project lại mỗi lần đọc — một property mà cấp phát bộ nhớ là một
    /// property sẽ bị gọi trong vòng lặp. Dùng <see cref="ImmutableArray{T}"/> vì lý do nêu trong
    /// ADR-025.
    /// </remarks>
    public ImmutableArray<ulong> Aliases { get; }

    /// <summary>Birth đã khai báo bao nhiêu alias.</summary>
    public int Count => _byAlias.Count;

    /// <summary>Tra tên metric đứng sau một alias.</summary>
    /// <param name="alias">Con số mà payload đã dùng.</param>
    /// <param name="metricName">Tên đã khai báo, khi alias đã biết.</param>
    /// <returns><see langword="true"/> khi alias đã được birth khai báo.</returns>
    public bool TryGetMetricName(ulong alias, out string? metricName)
    {
        if (_byAlias.TryGetValue(alias, out var definition))
        {
            metricName = definition.Name;
            return true;
        }

        metricName = null;
        return false;
    }

    // Dùng Frozen thay vì Dictionary thường: được build một lần cho mỗi birth, rồi được đọc cho mọi
    // message trong session tiếp theo — đúng cái hình dạng mà FrozenDictionary nhanh hơn, và cũng là
    // lựa chọn mà flat index của factory model đã dùng ở M1.
    internal static MetricAliasTable From(IReadOnlyDictionary<ulong, MetricDefinition> definitions) =>
        definitions.Count == 0
            ? Empty
            : new MetricAliasTable(definitions.ToFrozenDictionary());

    internal bool TryResolve(ulong alias, out MetricDefinition definition) =>
        _byAlias.TryGetValue(alias, out definition!);
}

/// <summary>Một dòng của birth: metric được gọi tên gì và mang kiểu gì.</summary>
internal sealed record MetricDefinition(string Name, DataType DataType);
