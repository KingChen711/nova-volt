using System.Collections.Frozen;

namespace Nvm.Ingestion.Publishing;

/// <summary>Liệt kê các signal code đủ điều kiện trở thành business fact thay vì chỉ là observation.</summary>
/// <remarks>
/// <para>
/// scope.md §5.5 vạch ra ranh giới và type này là nơi ranh giới đó được thể hiện trong code:
/// <i>"nếu nó thay đổi business state của một production unit thì đó là domain event; nếu nó là
/// continuous observation thì đó là telemetry"</i>. Một formation channel báo cáo voltage mỗi vài
/// giây là loại thứ hai. Capacity mà một cycle kết thúc ở đó là loại thứ nhất — nó có thể sau này
/// dùng để chấm điểm cell.
/// </para>
/// <para>
/// <b>Tính chung cuộc được mang bởi signal code và không gì khác.</b> Một cycler báo cáo
/// <c>Formation/Capacity</c> mỗi vài giây và một test station báo cáo capacity mà một cell kết thúc
/// ở đó là hai signal khác nhau, nên nhà máy đặt cho chúng hai cái tên khác nhau và chỉ cái thứ hai
/// từng được liệt vào đây. Quyết định theo cách khác — "nó có unit id, chắc là ai đó đã đánh giá
/// rồi" — chỉ đúng khi raw reading chưa có unit: khoảnh khắc M7 ánh xạ một channel vào cell đang nằm
/// trong đó, mọi điểm trên curve đều có được một unit id và cả curve bước thẳng lên bus.
/// </para>
/// <para>
/// Một dòng vẫn phải nêu tên unit mà nó nói về trước khi có thể được announce, nhưng đó là một
/// completeness check trên một event đã là business fact rồi, không phải phép thử để xác định nó có
/// phải business fact hay không.
/// </para>
/// <para>
/// Publish mọi thứ sẽ đặt hàng nghìn event mỗi giây lên một bus vốn tồn tại để mang các quyết định,
/// và sẽ làm điều đó một cách âm thầm: không gì fail cả, broker chỉ đơn giản là đầy dần, và event
/// store trở thành chính cái time-series database mà nó đã cố tình được tách riêng ra. Không publish
/// gì là lựa chọn an toàn mặc định vì telemetry dù sao cũng đã được lưu — reading không bao giờ mất,
/// chỉ là không được announce mà thôi.
/// </para>
/// </remarks>
public sealed class PublishedSignals
{
    private readonly FrozenSet<string> _signalCodes;

    /// <summary>Tạo filter từ các signal code đã cấu hình.</summary>
    /// <param name="signalCodes">Các signal code cần publish, đúng như nhà máy đánh vần chúng.</param>
    public PublishedSignals(IEnumerable<string>? signalCodes)
    {
        _signalCodes = (signalCodes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            // Ordinal, như mọi phép so sánh khác của một plant identifier trong hệ thống này. Signal
            // code đi vào natural key mà không được chuẩn hóa (C04), nên một phép so khớp
            // case-insensitive ở đây sẽ publish một event có SignalCode không bao giờ bằng cái đã
            // được cấu hình.
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Không publish gì cả. Mặc định, và cũng là thứ một nhà máy chưa quyết định nên chạy.</summary>
    public static PublishedSignals None { get; } = new([]);

    /// <summary>Có bao nhiêu signal code đang trong danh sách.</summary>
    public int Count => _signalCodes.Count;

    /// <summary>Một reading của signal này có được announce lên bus hay không.</summary>
    /// <param name="signalCode">Tên metric đúng như device đã khai báo.</param>
    public bool Includes(string signalCode) => _signalCodes.Contains(signalCode);
}
