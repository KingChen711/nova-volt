namespace Nvm.Time;

/// <summary>Biến một instant thành production day và shift mà một plant sẽ ghi nhận nó vào.</summary>
/// <remarks>
/// <para>
/// Domain function trung tâm của M3 (docs/scope.md §9/M3). Đây là một <b>function</b>, không phải một
/// query: không có gì ở đây đọc database, và câu trả lời của một plant chỉ phụ thuộc vào zone của nó,
/// shift table của nó và instant được hỏi tới.
/// </para>
/// <para>
/// Mọi method đều nhận một plant, và không method nào mặc định nó. Một câu trả lời calendar không có
/// site là một câu trả lời cross-site, và K3 không có chỗ cho điều đó — 05:59 ở Hải Phòng và 05:59 ở
/// Leipzig cách nhau tám giờ, và có hai ngày trong năm còn không phải đúng tám giờ cố định.
/// </para>
/// </remarks>
public interface IProductionCalendar
{
    /// <summary>Production day mà một instant thuộc về tại một plant.</summary>
    /// <param name="instant">Thời điểm, dưới dạng một điểm tuyệt đối trong thời gian.</param>
    /// <param name="siteId">Plant, ví dụ <c>NV1</c>.</param>
    /// <exception cref="UnknownSiteException">Plant không có calendar.</exception>
    ProductionDay GetProductionDay(DateTimeOffset instant, string siteId);

    /// <summary>Shift mà một instant rơi vào tại một plant.</summary>
    /// <param name="instant">Thời điểm, dưới dạng một điểm tuyệt đối trong thời gian.</param>
    /// <param name="siteId">Plant.</param>
    /// <exception cref="UnknownSiteException">Plant không có calendar.</exception>
    Shift GetShift(DateTimeOffset instant, string siteId);

    /// <summary>Một shift cho trước của một production day cho trước đã bắt đầu và kết thúc khi nào tại một plant.</summary>
    /// <param name="day">Production day.</param>
    /// <param name="shift">Shift.</param>
    /// <param name="siteId">Plant.</param>
    /// <returns>Hai instant tuyệt đối, half-open.</returns>
    /// <exception cref="UnknownSiteException">Plant không có calendar.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Plant không chạy shift đó.</exception>
    ShiftBoundaries GetShiftBoundaries(ProductionDay day, Shift shift, string siteId);

    /// <summary>Production day mà một plant đang ở ngay lúc này.</summary>
    /// <param name="siteId">Plant.</param>
    /// <exception cref="UnknownSiteException">Plant không có calendar.</exception>
    ProductionDay CurrentProductionDay(string siteId);

    /// <summary>Shift mà một plant đang ở ngay lúc này.</summary>
    /// <param name="siteId">Plant.</param>
    /// <exception cref="UnknownSiteException">Plant không có calendar.</exception>
    Shift CurrentShift(string siteId);
}
