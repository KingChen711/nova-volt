using System.Collections.Immutable;
using System.Globalization;

namespace Nvm.Time;

/// <summary>Shift table của một plant, đã được kiểm tra để phủ đồng hồ đúng một lần.</summary>
/// <remarks>
/// <para>
/// <b>Dữ liệu, không phải switch-case.</b> docs/scope.md §2.3 cho NovaVolt một bảng, và M10 mang đến
/// một site có bảng khác. Một bảng có thể được thay thế theo từng site; một <c>switch</c> trên
/// <see cref="Shift"/> rải rác khắp codebase thì không, và ngày ai đó thử làm vậy họ sẽ thấy các giờ
/// được viết ra ở bốn chỗ mà đã âm thầm trôi lệch nhau.
/// </para>
/// <para>
/// Check lúc dựng đối tượng là toàn bộ giá trị của type này: một bảng để trống 05:00 sẽ khiến một
/// measurement lấy lúc 05:00 hoàn toàn không có shift nào, và một bảng phủ 05:00 hai lần sẽ cho nó
/// hai shift. Cả hai đều bị phát hiện ở đây thay vì trong một báo cáo sáu tháng sau.
/// </para>
/// <para>
/// <b>Thứ tự mang ý nghĩa.</b> Các dòng được giữ nguyên như khi được truyền vào, vì dòng đầu tiên mở
/// ra production day — đó là điều khiến 06:00 trở thành boundary thay vì nửa đêm. Sắp xếp lại chúng
/// sẽ vứt bỏ điều đó với một bảng có ngày bắt đầu lúc 22:00.
/// </para>
/// </remarks>
public sealed class ShiftSchedule
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private readonly ImmutableArray<ShiftDefinition> _definitions;

    private ShiftSchedule(ImmutableArray<ShiftDefinition> definitions)
    {
        _definitions = definitions;
        DayStart = definitions[0].LocalStart;
    }

    /// <summary>Bảng của NovaVolt: A 06–14, B 14–22, C 22–06 local (docs/scope.md §2.3).</summary>
    public static ShiftSchedule Default { get; } = Create(
    [
        new ShiftDefinition(Shift.A, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
        new ShiftDefinition(Shift.B, new TimeOnly(14, 0), TimeSpan.FromHours(8)),
        new ShiftDefinition(Shift.C, new TimeOnly(22, 0), TimeSpan.FromHours(8)),
    ]);

    /// <summary>Các dòng, theo thứ tự chạy, bắt đầu bằng dòng mở ra production day.</summary>
    /// <remarks>
    /// <see cref="ImmutableArray{T}"/> thay vì <c>IReadOnlyList</c> — <c>ADR-025</c>. Một caller có thể
    /// cast ngược lại thành array sẽ có thể sắp xếp lại một bảng mà các bất biến của nó chỉ được kiểm
    /// tra một lần, lúc dựng đối tượng, và không bao giờ kiểm tra lại.
    /// </remarks>
    public ImmutableArray<ShiftDefinition> Definitions => _definitions;

    /// <summary>Wall clock reading mà một production day bắt đầu tại đó. 06:00 với NovaVolt.</summary>
    /// <remarks>
    /// Đọc từ dòng đầu tiên thay vì viết ra lần thứ hai: một site có shift mở màn bắt đầu lúc 07:00 sẽ
    /// có production day bắt đầu lúc 07:00, và không ai phải nhớ đi đổi một hằng số để nói điều đó.
    /// </remarks>
    public TimeOnly DayStart { get; }

    /// <summary>Dựng một shift table, từ chối một bảng không phủ đồng hồ đúng một lần.</summary>
    /// <param name="definitions">
    /// Các dòng theo thứ tự chạy, shift mở màn trước tiên. Bất kỳ điểm bắt đầu nào cũng được chấp
    /// nhận miễn là bảng liên tục khi đi từ dòng đầu tiên.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Bảng rỗng, nêu tên một shift hai lần, không liên tục, hoặc không cộng đủ 24 giờ.
    /// </exception>
    public static ShiftSchedule Create(IEnumerable<ShiftDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var rows = definitions.ToImmutableArray();

        if (rows.IsEmpty)
        {
            throw new ArgumentException("A shift table needs at least one shift.", nameof(definitions));
        }

        if (rows.Select(row => row.Shift).Distinct().Count() != rows.Length)
        {
            throw new ArgumentException("A shift table names each shift once.", nameof(definitions));
        }

        var total = TimeSpan.Zero;

        foreach (var row in rows)
        {
            if (row.NominalLength <= TimeSpan.Zero || row.NominalLength > Day)
            {
                throw new ArgumentException(
                    $"Shift {row.Shift} lasts {row.NominalLength}, which is not a length a shift can have.",
                    nameof(definitions));
            }

            total += row.NominalLength;
        }

        if (total != Day)
        {
            throw new ArgumentException(
                $"The shift table covers {total} of the clock. It has to cover exactly 24 hours: an "
                + "uncovered minute is a measurement that belongs to no shift, and a doubly covered one "
                + "is a measurement that belongs to two.",
                nameof(definitions));
        }

        // Đi qua có wrap, nên shift vượt qua nửa đêm không phải một trường hợp đặc biệt ở đây — nó đơn
        // giản là dòng có dòng kế tiếp lại chính là dòng đầu tiên. Cùng với tổng 24 giờ, tính liên tục
        // là cái loại trừ cả khoảng trống lẫn chồng lấn.
        for (var index = 0; index < rows.Length; index++)
        {
            var current = rows[index];
            var next = rows[(index + 1) % rows.Length];
            var expected = current.LocalStart.Add(current.NominalLength);

            if (expected != next.LocalStart)
            {
                throw new ArgumentException(
                    $"Shift {current.Shift} ends at {Format(expected)} but shift {next.Shift} starts at "
                    + $"{Format(next.LocalStart)}. The table has to be contiguous.",
                    nameof(definitions));
            }
        }

        return new ShiftSchedule(rows);
    }

    /// <summary>Shift phủ một wall clock reading.</summary>
    /// <param name="localTimeOfDay">Một reading của đồng hồ site, không ngày, không offset.</param>
    /// <remarks>
    /// Toàn phần nhờ cấu trúc: bảng đã được kiểm tra để phủ đồng hồ đúng một lần, nên luôn có một câu
    /// trả lời và không bao giờ có hai.
    /// </remarks>
    public ShiftDefinition ShiftAt(TimeOnly localTimeOfDay)
    {
        // Phép trừ TimeOnly wrap ở nửa đêm và không bao giờ trả về số âm, đây chính là điều cho phép
        // shift vượt qua nửa đêm rơi vào cùng phép toán như hai shift còn lại.
        var sinceDayStart = localTimeOfDay - DayStart;
        var elapsed = TimeSpan.Zero;

        foreach (var definition in _definitions)
        {
            elapsed += definition.NominalLength;

            if (sinceDayStart < elapsed)
            {
                return definition;
            }
        }

        // Không thể đạt tới trong khi check lúc dựng đối tượng còn giữ đúng. Ném lỗi thay vì trả về
        // dòng cuối cùng nghĩa là một thay đổi tương lai làm hỏng bất biến sẽ bị phát hiện ở đây, thay
        // vì âm thầm filing những giờ khuya dưới sai shift.
        throw new InvalidOperationException(
            $"No shift covers {Format(localTimeOfDay)}, which a validated table cannot happen to.");
    }

    /// <summary>Dòng cho một shift.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Bảng không chạy shift đó.</exception>
    public ShiftDefinition Definition(Shift shift)
    {
        foreach (var definition in _definitions)
        {
            if (definition.Shift == shift)
            {
                return definition;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(shift), shift, "This plant does not run that shift.");
    }

    /// <summary>Một shift bắt đầu sâu bao xa vào trong production day.</summary>
    /// <param name="shift">Shift.</param>
    /// <remarks>
    /// Bằng không với shift mở màn, mười sáu giờ với shift C. Đây là con số biến "shift C của
    /// production day 25" thành một wall clock reading trên một ngày lịch, và nó được suy ra từ bảng
    /// thay vì giả định là bội số của tám giờ.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Bảng không chạy shift đó.</exception>
    public TimeSpan OffsetIntoDay(Shift shift)
    {
        var elapsed = TimeSpan.Zero;

        foreach (var definition in _definitions)
        {
            if (definition.Shift == shift)
            {
                return elapsed;
            }

            elapsed += definition.NominalLength;
        }

        throw new ArgumentOutOfRangeException(nameof(shift), shift, "This plant does not run that shift.");
    }

    private static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
