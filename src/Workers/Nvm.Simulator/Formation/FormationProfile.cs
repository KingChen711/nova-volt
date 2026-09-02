namespace Nvm.Simulator.Formation;

/// <summary>Những gì một formation channel đọc được tại một thời điểm của một cycle.</summary>
/// <param name="Step">Cycle đang ở stage nào trong năm stage.</param>
/// <param name="Volts">Điện áp cell.</param>
/// <param name="Amperes">Dòng điện. Dương là đang sạc cell, âm là đang xả.</param>
/// <param name="Celsius">Nhiệt độ bề mặt cell.</param>
/// <param name="AmpHours">Điện lượng đã tích lũy tính đến thời điểm đó.</param>
public readonly record struct FormationSample(
    FormationStep Step,
    double Volts,
    double Amperes,
    double Celsius,
    double AmpHours);

/// <summary>Năm stage của một formation cycle, theo đúng thứ tự.</summary>
public enum FormationStep
{
    /// <summary>Cell nghỉ sau khi được nạp vào, để điện áp hở mạch của nó ổn định lại.</summary>
    RestBeforeCharge = 1,

    /// <summary>Constant current: dòng điện được giữ cố định và điện áp tăng dần.</summary>
    ConstantCurrentCharge = 2,

    /// <summary>Constant voltage: điện áp được giữ ở mức trần và dòng điện giảm dần.</summary>
    ConstantVoltageCharge = 3,

    /// <summary>Cell nghỉ và điện áp giãn ra khỏi mức điện áp sạc.</summary>
    RestAfterCharge = 4,

    /// <summary>Lần xả đầu tiên, đây là nguồn gốc của dung lượng đo được.</summary>
    Discharge = 5,
}

/// <summary>Hình dạng của một formation cycle, là hàm của việc cell đã đi được bao xa vào trong nó.</summary>
/// <remarks>
/// <para>
/// Một hàm thuần (pure function) của thời gian đã trôi qua trong cycle, và mọi thứ khác trong
/// simulator đều phụ thuộc vào điều đó. Chính điều này cho phép một cycle được chạy nhanh gấp nghìn
/// lần mà vẫn tạo ra <b>cùng những measurement</b> — các sample được lấy ở những điểm cố định của
/// process time, nên việc nén wall clock chỉ thay đổi thời gian chạy hết một run chứ không thay đổi
/// gì khác.
/// </para>
/// <para>
/// <b>Không phải ngẫu nhiên.</b> Giá trị ngẫu nhiên sẽ khiến mọi thứ ở downstream trông như vẫn hoạt
/// động trong khi không ai có thể nhận ra rằng một phép tính đã bắt đầu tính sai đường cong, vì sẽ
/// không có đường cong đúng nào để so sánh — và sai sót đó sẽ nằm im cho tới tận M8. Điện áp tăng
/// theo một đường cong sạc, dòng điện chuyển bậc giữa CC và CV, nhiệt độ đi theo dòng điện, và dung
/// lượng là tích phân của dòng điện đó.
/// </para>
/// <para>
/// Đây là một hình dạng, không phải một mô hình. Không ai nên dự đoán hóa học của cell từ đây; điểm
/// mấu chốt là đường cong này có những đặc điểm mà một đường cong thật có — một nhánh CC dốc lên, một
/// đuôi CV, một mặt phẳng discharge với một khúc gãy (knee) ở cuối — để code đọc nó có thể được kiểm
/// chứng là đang đọc đúng.
/// </para>
/// </remarks>
public sealed class FormationProfile
{
    private static readonly TimeSpan RestBefore = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChargeCcEnd = TimeSpan.FromHours(8);
    private static readonly TimeSpan ChargeCvEnd = TimeSpan.FromHours(11);
    private static readonly TimeSpan RestAfterEnd = TimeSpan.FromHours(11.5);

    private const double RestVolts = 3.00;
    private const double CeilingVolts = 4.20;
    private const double RelaxedVolts = 4.15;
    private const double EmptyVolts = 3.00;
    private const double ChargeAmperes = 0.50;
    private const double DischargeAmperes = -0.40;
    private const double AmbientCelsius = 25.0;
    private const double CelsiusPerAmpere = 14.0;

    /// <summary>Hằng số suy giảm của đuôi CV: dòng điện giảm xuống còn khoảng 5% giá trị CC vào cuối.</summary>
    private const double CvDecay = 3.0;

    /// <summary>Cycle tham chiếu: 18 giờ, là điểm giữa của khoảng 12–24 giờ.</summary>
    public static FormationProfile Default { get; } = new(TimeSpan.FromHours(18));

    /// <summary>Tạo một profile với độ dài cho trước.</summary>
    /// <param name="cycleDuration">Một cell ở trong channel bao lâu.</param>
    /// <exception cref="ArgumentOutOfRangeException">Cycle ngắn hơn chính các stage của nó.</exception>
    public FormationProfile(TimeSpan cycleDuration)
    {
        // Ranh giới các stage là tuyệt đối, nên một cycle ngắn hơn ranh giới cuối cùng trong số đó sẽ
        // đặt cell vào một stage kết thúc trước khi nó bắt đầu.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cycleDuration, RestAfterEnd);

        CycleDuration = cycleDuration;
    }

    /// <summary>Một cell ở trong channel bao lâu.</summary>
    public TimeSpan CycleDuration { get; }

    /// <summary>Đọc channel tại một điểm trong cycle.</summary>
    /// <param name="elapsed">Cell đã đi được bao xa vào trong cycle.</param>
    /// <exception cref="ArgumentOutOfRangeException">Điểm này nằm ngoài cycle.</exception>
    public FormationSample At(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elapsed, CycleDuration);

        if (elapsed < RestBefore)
        {
            return Sample(FormationStep.RestBeforeCharge, RestVolts, 0, 0);
        }

        if (elapsed < ChargeCcEnd)
        {
            var progress = Fraction(elapsed - RestBefore, ChargeCcEnd - RestBefore);

            // Lũy thừa 0.8 thay vì một đường thẳng: một nhánh CC thật tăng nhanh khi ra khỏi khúc
            // gãy (knee) ở mức thấp rồi phẳng dần khi tiến gần tới mức trần.
            return Sample(
                FormationStep.ConstantCurrentCharge,
                RestVolts + ((CeilingVolts - RestVolts) * Math.Pow(progress, 0.8)),
                ChargeAmperes,
                ChargeAmperes * (elapsed - RestBefore).TotalHours);
        }

        var ccAmpHours = ChargeAmperes * (ChargeCcEnd - RestBefore).TotalHours;

        if (elapsed < ChargeCvEnd)
        {
            var progress = Fraction(elapsed - ChargeCcEnd, ChargeCvEnd - ChargeCcEnd);
            var hours = (ChargeCvEnd - ChargeCcEnd).TotalHours;

            // Giữ ở mức trần trong khi dòng điện suy giảm; điện lượng tích lũy là tích phân của độ
            // suy giảm đó, đó là lý do đường cong dung lượng bẻ cong ở đây thay vì tiếp tục thẳng.
            return Sample(
                FormationStep.ConstantVoltageCharge,
                CeilingVolts,
                ChargeAmperes * Math.Exp(-CvDecay * progress),
                ccAmpHours + (ChargeAmperes * hours * (1 - Math.Exp(-CvDecay * progress)) / CvDecay));
        }

        var chargedAmpHours = ccAmpHours
            + (ChargeAmperes * (ChargeCvEnd - ChargeCcEnd).TotalHours * (1 - Math.Exp(-CvDecay)) / CvDecay);

        if (elapsed < RestAfterEnd)
        {
            var progress = Fraction(elapsed - ChargeCvEnd, RestAfterEnd - ChargeCvEnd);

            return Sample(
                FormationStep.RestAfterCharge,
                CeilingVolts - ((CeilingVolts - RelaxedVolts) * (1 - Math.Exp(-4 * progress))),
                0,
                chargedAmpHours);
        }

        var discharged = Fraction(elapsed - RestAfterEnd, CycleDuration - RestAfterEnd);

        // Lũy thừa 2.2 đặt khúc gãy (knee) ở cuối: điện áp nằm trên một mặt phẳng trong phần lớn thời
        // gian discharge rồi rơi nhanh về sau, đó chính là đặc điểm mà một quy tắc chấm điểm tìm kiếm.
        return Sample(
            FormationStep.Discharge,
            RelaxedVolts - ((RelaxedVolts - EmptyVolts) * Math.Pow(discharged, 2.2)),
            DischargeAmperes,
            chargedAmpHours + (DischargeAmperes * (elapsed - RestAfterEnd).TotalHours));
    }

    private static double Fraction(TimeSpan elapsed, TimeSpan span) =>
        Math.Clamp(elapsed / span, 0, 1);

    // Nhiệt độ đi theo dòng điện chứ không theo đồng hồ: cell nóng lên khi điện lượng đi qua nó và
    // nằm ở mức ambient khi nó nghỉ. Không có độ trễ (lag) nào được mô hình hóa, vì một độ trễ cần
    // state, và state sẽ khiến profile phụ thuộc vào tần suất nó được lấy mẫu — đúng là tính chất
    // phải sống sót qua time compression.
    private static FormationSample Sample(FormationStep step, double volts, double amperes, double ampHours) =>
        new(step, volts, amperes, AmbientCelsius + (CelsiusPerAmpere * Math.Abs(amperes)), ampHours);
}
