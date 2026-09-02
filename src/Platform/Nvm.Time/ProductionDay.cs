using System.Globalization;

namespace Nvm.Time;

/// <summary>Chu kỳ sản xuất mà một measurement thuộc về, được nêu tên bằng một ngày trên lịch.</summary>
/// <remarks>
/// <para>
/// Một production day <b>không phải</b> một calendar day. Đó là chu kỳ bắt đầu khi shift A bắt đầu —
/// 06:00 local — và chạy cho tới khi shift A bắt đầu lần nữa. Shift C từ 22:00 ngày 25 tới 06:00 ngày
/// 26 thuộc về production day <c>2026-08-25</c>, nên sáu giờ trong đó rơi vào một ngày lịch khác với
/// ngày mà nó được filing dưới đó (docs/scope.md §2.3).
/// </para>
/// <para>
/// <b>Vì sao dùng một type riêng thay vì <see cref="DateOnly"/>.</b> Hai thứ này giống hệt nhau về cấu
/// trúc, và chính điều đó là mối nguy hiểm: nếu một production day là một <see cref="DateOnly"/>,
/// không gì ngăn được ai đó gán cho nó kết quả của <c>CAST(device_timestamp AS date)</c> hoặc của
/// calendar date theo local, và compiler sẽ đồng ý. Cả hai đều sai trong sáu giờ trên mỗi hai mươi bốn
/// giờ, và lỗi nổi lên nhiều tháng sau dưới dạng "shift C trông ngắn" trong một báo cáo hàng tháng.
/// Không có conversion ngầm theo chiều nào chính vì lý do đó — để nguyên type là một quyết định có chủ
/// đích, buộc gọi tới <see cref="Date"/>.
/// </para>
/// </remarks>
public readonly record struct ProductionDay : IComparable<ProductionDay>
{
    private ProductionDay(DateOnly date) => Date = date;

    /// <summary>Ngày lịch mà chu kỳ bắt đầu trên đó, theo local time của site.</summary>
    /// <remarks>
    /// Cố tình là một property chứ không phải một conversion ngầm. Đọc nó là một khẳng định rằng caller
    /// muốn nói tới nhãn của chu kỳ, không phải "ngày mà instant này rơi vào".
    /// </remarks>
    public DateOnly Date { get; }

    /// <summary>Nêu tên một production day bằng ngày lịch mà shift A của nó bắt đầu trên đó.</summary>
    public static ProductionDay On(DateOnly date) => new(date);

    /// <summary>Nêu tên một production day bằng năm, tháng và ngày.</summary>
    public static ProductionDay On(int year, int month, int day) => new(new DateOnly(year, month, day));

    /// <summary>Production day tiếp theo sau ngày này.</summary>
    public ProductionDay Next() => new(Date.AddDays(1));

    /// <summary>Production day trước ngày này.</summary>
    public ProductionDay Previous() => new(Date.AddDays(-1));

    /// <summary>Có bao nhiêu production day nằm giữa ngày này và một ngày khác.</summary>
    public int DaysUntil(ProductionDay other) => other.Date.DayNumber - Date.DayNumber;

    /// <inheritdoc />
    public int CompareTo(ProductionDay other) => Date.CompareTo(other.Date);

    /// <summary>Một production day có đứng trước một production day khác hay không.</summary>
    public static bool operator <(ProductionDay left, ProductionDay right) => left.CompareTo(right) < 0;

    /// <summary>Một production day có đứng sau một production day khác hay không.</summary>
    public static bool operator >(ProductionDay left, ProductionDay right) => left.CompareTo(right) > 0;

    /// <summary>Một production day có đứng trước hoặc trùng một production day khác hay không.</summary>
    public static bool operator <=(ProductionDay left, ProductionDay right) => left.CompareTo(right) <= 0;

    /// <summary>Một production day có đứng sau hoặc trùng một production day khác hay không.</summary>
    public static bool operator >=(ProductionDay left, ProductionDay right) => left.CompareTo(right) >= 0;

    /// <summary>Ngày, dưới dạng <c>2026-08-25</c>.</summary>
    /// <remarks>
    /// Invariant và ISO, không bao giờ theo culture hiện tại: chuỗi này đi vào report, log line và
    /// SQL, và một máy ở locale <c>de-DE</c> viết ra <c>25.08.2026</c> sẽ tạo ra một cách viết thứ hai
    /// cho cùng một ngày.
    /// </remarks>
    public override string ToString() => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
