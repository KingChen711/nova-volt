namespace Nvm.Time;

/// <summary>Ba shift tạo nên một production day.</summary>
/// <remarks>
/// <para>
/// Các chữ cái là của chính plant (docs/scope.md §2.3), không phải một sự bịa đặt: một supervisor nói
/// "ca C" và một báo cáo ERP viết <c>C</c>, nên enum này viết đúng theo cách shop floor viết.
/// </para>
/// <para>
/// <see cref="C"/> là shift duy nhất vượt qua nửa đêm, và mọi trường hợp trớ trêu trong assembly này
/// đều đến từ đúng một sự thật đó — một night shift thuộc về ngày nó <b>bắt đầu</b>, là ngày trước
/// ngày lịch mà phần lớn số giờ của nó rơi vào.
/// </para>
/// </remarks>
public enum Shift
{
    /// <summary>Ca sáng, 06:00 đến 14:00 local.</summary>
    A = 1,

    /// <summary>Ca chiều, 14:00 đến 22:00 local.</summary>
    B = 2,

    /// <summary>Ca đêm, 22:00 đến 06:00 local ngày lịch kế tiếp.</summary>
    C = 3,
}
