namespace Nvm.Time;

/// <summary>Một shift của một production day thực sự bắt đầu và kết thúc khi nào.</summary>
/// <param name="Day">Production day mà shift thuộc về.</param>
/// <param name="Shift">Shift nào.</param>
/// <param name="Start">Instant nó bắt đầu, bao gồm.</param>
/// <param name="End">Instant nó kết thúc, không bao gồm.</param>
/// <remarks>
/// <para>
/// <b>Hai instant tuyệt đối, không phải hai clock reading.</b> "22:00 đến 06:00" là điều bảng shift
/// trên tường nói; nó không đủ để dùng chọn dòng dữ liệu, vì vào hai ngày mỗi năm ở DE1 hai reading đó
/// cách nhau bảy giờ và vào hai ngày khác chúng cách nhau chín giờ. Mọi thứ cần đếm gì đó trong phạm
/// vi một shift đều dùng những instant này.
/// </para>
/// <para>
/// <b>Half-open, <c>[Start, End)</c>.</b> Một interval đóng sẽ cho instant lúc 14:00 thuộc cả shift A
/// lẫn shift B, và một measurement được đếm trong hai shift là một measurement bị đếm hai lần — đủ
/// nhỏ để trông như nhiễu, đủ lớn để làm lệch một con số yield. Điểm kết thúc của một shift chính xác
/// là điểm bắt đầu của shift kế tiếp, nhờ cấu trúc.
/// </para>
/// </remarks>
public readonly record struct ShiftBoundaries(
    ProductionDay Day,
    Shift Shift,
    DateTimeOffset Start,
    DateTimeOffset End)
{
    /// <summary>Shift thực sự kéo dài bao lâu.</summary>
    /// <remarks>
    /// Tám giờ vào 363 ngày một năm ở DE1, bảy giờ vào một ngày và chín giờ vào một ngày khác. Đó
    /// không phải một lỗi cần sửa — đó là sự thật mà một phép tính OEE chia cho tám giờ hard-code sẽ
    /// tính sai, rồi báo cáo thành một sụt giảm hiệu suất 12,5% chưa từng xảy ra.
    /// </remarks>
    public TimeSpan Duration => End - Start;

    /// <summary>Một instant có rơi vào shift này hay không.</summary>
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;
}
