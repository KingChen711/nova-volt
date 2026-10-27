namespace Nvm.Time;

/// <summary>Một dòng trong shift table của một plant: một shift bắt đầu khi nào và kéo dài bao lâu theo nominal.</summary>
/// <param name="Shift">Dòng này mô tả shift nào.</param>
/// <param name="LocalStart">Wall clock reading mà shift bắt đầu, theo giờ riêng của site.</param>
/// <param name="NominalLength">
/// Shift kéo dài bao lâu <b>trên đồng hồ</b> — tám giờ cho mỗi shift ở đây.
/// </param>
/// <remarks>
/// <para>
/// <b>Từ "nominal" mang ý nghĩa cốt lõi.</b> Đây là độ dài theo wall-clock, không phải độ dài trôi
/// qua thực tế. Vào hai ngày mỗi năm DE1 đổi giờ, shift C chạy nominal tám giờ và thực tế bảy hoặc
/// chín giờ, và sự khác biệt đó không phải một lỗi ở con số nào cả: shift thật sự bắt đầu lúc 22:00
/// và thật sự kết thúc lúc 06:00, và đồng hồ thật sự đã bỏ qua một giờ ở giữa. Bất cứ cái gì cần độ
/// dài trôi qua thực tế phải hỏi production calendar về boundary của shift rồi trừ đi, không bao giờ
/// nhân giá trị này với bất kỳ cái gì.
/// </para>
/// <para>
/// <see cref="TimeOnly"/> thay vì <see cref="DateTimeOffset"/> là có chủ đích: một shift table là một
/// phát biểu về các clock reading lặp lại mỗi ngày, và nó không có offset vì offset là một thuộc tính
/// của site và của ngày, không phải của bảng.
/// </para>
/// </remarks>
public readonly record struct ShiftDefinition(Shift Shift, TimeOnly LocalStart, TimeSpan NominalLength);
