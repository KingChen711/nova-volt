namespace Nvm.Simulator.Faults;

/// <summary>Ba kiểu mà nhà máy này được phép hành xử sai, và sai nặng tới đâu.</summary>
/// <remarks>
/// <para>
/// Mọi rate mặc định là <b>0</b>: một simulator không ai cấu hình gì sẽ tạo ra một nhà máy hành xử
/// đúng đắn. Các lab tự bật chúng lên, đây chính là cách sắp xếp M2 cần — một fault mặc định bật sẵn
/// sẽ bị quên mất, và khi đó mọi con số milestone tạo ra đều mang một thành phần không ai khai báo.
/// </para>
/// <para>
/// Sai lầm ngược lại là điều <c>R-M2-1</c> cảnh báo: chạy phép đối chiếu trong khi fault vẫn đang
/// tắt, thấy nó khớp, rồi đánh dấu D1 đã xong mà không kiểm tra được gì cả. Đó là lý do run report
/// ghi lại các con số này ngay bên cạnh tổng số — một run không chặn được duplicate nào thì vẫn hiện
/// rõ ra thay vì trông chỉ đơn giản là không có gì đáng chú ý.
/// </para>
/// </remarks>
public sealed class SimulatorFaults
{
    /// <summary>Tỉ lệ message được publish bị gửi lần thứ hai. Lab dùng <c>0.10</c>.</summary>
    /// <remarks>
    /// Là <b>cùng một</b> message, không phải một message khác giống nó. Xem
    /// <see cref="FaultInjectingPublisher"/> để biết vì sao sự khác biệt này quyết định D1 có đo
    /// được gì hay không.
    /// </remarks>
    public double DuplicateRate { get; set; }

    /// <summary>Tỉ lệ device có đồng hồ sai. Lab dùng <c>0.10</c>.</summary>
    /// <remarks>
    /// Tính theo <b>device</b>, không phải theo message. Một pin CMOS hỏng thì sai trên mọi reading
    /// mà channel đó từng lấy, và một fault làm lệch ngẫu nhiên một phần mười số message sẽ là một
    /// fault không phần cứng nào có — tệ hơn, nó sẽ là một fault mà cờ <c>Drifted</c> không thể dùng
    /// để nhóm lại một cách có ích.
    /// </remarks>
    public double DriftedDeviceRate { get; set; }

    /// <summary>Đồng hồ sai thì sai bao xa. Áp dụng dạng cộng hoặc trừ, theo từng device.</summary>
    public TimeSpan ClockDrift { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Khoảng cách trung bình giữa hai lần mất kết nối. Bằng 0 thì tắt fault này.</summary>
    /// <remarks>
    /// Là một giá trị trung bình chứ không phải một chu kỳ cố định. Các lần rớt mạng xảy ra theo một
    /// Poisson process, nên khoảng cách giữa chúng theo phân phối mũ; một khoảng cố định sẽ khiến
    /// một run rơi vào một nhịp điệu và để cho bất cứ thứ gì phía sau vô tình phụ thuộc vào nó.
    /// </remarks>
    public TimeSpan DropoutMeanInterval { get; set; }

    /// <summary>Một lần mất kết nối kéo dài bao lâu.</summary>
    public TimeSpan DropoutDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Seed cho xúc xắc fault, để một run có thể chạy lại y hệt.</summary>
    /// <remarks>
    /// Một lab không thể chạy lại với đúng các fault như cũ là một lab mà kết quả bất ngờ của nó
    /// không thể điều tra được — chỉ có thể roll lại cho tới khi nó biến mất.
    /// </remarks>
    public int Seed { get; set; } = 20260828;

    /// <summary>Có bất cứ thứ gì đang được bật lên hay không.</summary>
    public bool AnyEnabled =>
        DuplicateRate > 0 || DriftedDeviceRate > 0 || DropoutMeanInterval > TimeSpan.Zero;

    /// <summary>Từ chối những cấu hình không mô tả một fault hợp lệ.</summary>
    /// <exception cref="InvalidOperationException">Một giá trị nằm ngoài phạm vi cho phép.</exception>
    public void Validate()
    {
        if (DuplicateRate is < 0 or > 1)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The duplicate rate is a share of messages and must be between 0 and 1, not {DuplicateRate}."));
        }

        if (DriftedDeviceRate is < 0 or > 1)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The drifted device rate is a share of devices and must be between 0 and 1, not {DriftedDeviceRate}."));
        }

        if (DropoutMeanInterval > TimeSpan.Zero && DropoutDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "A dropout that lasts no time is not a dropout. Set a duration or switch the fault off.");
        }
    }
}
