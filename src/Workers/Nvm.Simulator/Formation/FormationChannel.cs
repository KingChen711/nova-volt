using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.Simulator.Formation;

/// <summary>Một channel sạc của một formation cycler, có một cell nằm trong đó.</summary>
/// <remarks>
/// <para>
/// Giữ hai mảnh state mà một channel thật có: cell nào đang được nạp vào, và lần cuối nó báo gì cho
/// ai đó. Mảnh thứ hai là thứ khiến report-by-exception khả thi — một reading chỉ được gửi khi nó đã
/// di chuyển đủ xa qua khỏi deadband, đó là lý do một channel đang rảnh thì im lặng và một channel
/// đang ở đuôi CV thì gần như im lặng luôn.
/// </para>
/// <para>
/// Channel tạo ra reading, không tạo ra message. Dựng một Sparkplug message cần tới sequence number
/// của edge node, và số đó thuộc về node chứ không thuộc về bất kỳ device nào bên dưới nó — xem
/// <see cref="FormationLine"/>.
/// </para>
/// </remarks>
public sealed class FormationChannel
{
    /// <summary>Tên metric và alias mà birth gán cho chúng.</summary>
    private const string VoltageMetric = "Formation/Voltage";
    private const string CurrentMetric = "Formation/Current";
    private const string TemperatureMetric = "Formation/Temperature";
    private const string CapacityMetric = "Formation/Capacity";
    private const string StepMetric = "Formation/StepIndex";
    private const string CellSerialMetric = "Formation/CellSerial";

    private const ulong VoltageAlias = 1;
    private const ulong CurrentAlias = 2;
    private const ulong TemperatureAlias = 3;
    private const ulong CapacityAlias = 4;
    private const ulong StepAlias = 5;
    private const ulong CellSerialAlias = 6;

    // Deadband, theo đơn vị của từng signal. Chọn sao cho chặng CC báo đều đặn còn các đoạn rest thì
    // gần như không báo gì, đó chính là hình dạng traffic của một line thật: channel im lặng mới là
    // chuyện bình thường.
    private const double VoltageDeadband = 0.001;
    private const double CurrentDeadband = 0.001;
    private const double TemperatureDeadband = 0.05;
    private const double CapacityDeadband = 0.001;

    private readonly FormationProfile _profile;
    private readonly double _spread;

    private double _lastVolts;
    private double _lastAmperes;
    private double _lastCelsius;
    private double _lastAmpHours;
    private FormationStep _lastStep;

    // Thời điểm của lần declare gần nhất, đây là thứ giúp phân biệt một rebirth với một cell mới.
    private DateTimeOffset? _lastDeclaredAt;

    /// <summary>Tạo một channel trống cho tới khi có cell được nạp vào.</summary>
    /// <param name="path">Channel nằm ở đâu, ví dụ <c>…/FORM-01/FORM-01-CH-0001</c>.</param>
    /// <param name="profile">Hình dạng cycle mà mọi cell trong channel này đi theo.</param>
    public FormationChannel(EquipmentPath path, FormationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(profile);

        Path = path;
        _profile = profile;
        _spread = SpreadOf(path.Code);
    }

    /// <summary>Channel này nằm ở đâu.</summary>
    public EquipmentPath Path { get; }

    /// <summary>Cell hiện đang nằm trong channel, hoặc null nếu channel đang trống.</summary>
    public string? CellSerial { get; private set; }

    /// <summary>Channel này đã lấy bao nhiêu reading kể từ khi được tạo.</summary>
    /// <remarks>
    /// <para>
    /// Vế trái của phép đối chiếu trong D1, và nó đếm những gì <b>thiết bị đã làm</b>: mọi metric mà
    /// một <c>DBIRTH</c> khai báo, và mọi signal mà một sample tìm thấy vượt qua deadband. Cell
    /// serial cũng nằm trong đó, vì gateway forward nó và pipeline lưu một dòng cho nó giống như bất
    /// kỳ metric khai báo nào khác; chỉ có protocol metric là bị loại ra, và chúng thuộc về node chứ
    /// không thuộc về một channel.
    /// </para>
    /// <para>
    /// Được đếm tại nơi reading <b>được lấy</b>, trước bất kỳ bước transport nào. Đếm ở phía bên kia
    /// của một lần publish trông có vẻ chặt chẽ hơn nhưng lại là điều ngược lại: một batch mà worker
    /// sau đó bỏ dở sẽ khiến vế này của phép đối chiếu dừng lại đúng lúc số dòng nó nợ rời khỏi vế
    /// kia, và hai vế sẽ khớp nhau qua một mất mát mà không ai nhìn thấy được. Việc đường truyền sau
    /// đó làm gì với một reading là con số riêng của đường truyền phải giữ —
    /// <see cref="SimulatorWorker.AbandonedMeasurements"/>.
    /// </para>
    /// </remarks>
    public long MeasurementCount { get; private set; }

    /// <summary>Khai báo channel: mọi metric, kèm tên, alias và giá trị hiện tại.</summary>
    /// <param name="cellSerial">Cell đang nằm trong channel.</param>
    /// <param name="elapsed">Cell đó đã đi được bao xa trong cycle của nó.</param>
    /// <param name="at">Đồng hồ device cho lần khai báo này.</param>
    /// <remarks>
    /// Bao trùm cả hai dịp một <c>DBIRTH</c> được publish, vì chúng là cùng một message: một cell
    /// mới được nạp vào, và node kết nối lại nên phải khai báo lại mọi thứ. Elapsed time là một
    /// tham số chính vì lý do đó — một lần reconnect xảy ra ở bất cứ đâu trên cycle mà nó đang rơi
    /// vào. Nó cũng reset trạng thái deadband, nên reading đầu tiên sau một birth luôn được gửi.
    /// </remarks>
    public ImmutableArray<DeviceReading> Declare(string cellSerial, TimeSpan elapsed, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cellSerial);

        CellSerial = cellSerial;

        var sample = Shift(_profile.At(elapsed));

        Remember(sample);

        ImmutableArray<DeviceReading> declared =
        [
            Reading(VoltageMetric, VoltageAlias, new MetricValue.Real(sample.Volts), at),
            Reading(CurrentMetric, CurrentAlias, new MetricValue.Real(sample.Amperes), at),
            Reading(TemperatureMetric, TemperatureAlias, new MetricValue.Real(sample.Celsius), at),
            Reading(CapacityMetric, CapacityAlias, new MetricValue.Real(sample.AmpHours), at),
            Reading(StepMetric, StepAlias, new MetricValue.Integral((long)sample.Step), at),
            Reading(CellSerialMetric, CellSerialAlias, new MetricValue.Text(cellSerial), at),
        ];

        // Đếm từ chính mảng bên cạnh, không bao giờ từ một con số literal. Một con "năm" hard-code
        // đứng cạnh sáu reading từng khiến run report báo thiếu mất một cái mỗi DBIRTH, trên mọi
        // channel, suốt cả một run — và không có gì fail cả: phép đối chiếu đơn giản là ra thiếu và
        // đọc như thể mất dữ liệu.
        //
        // Và chỉ đếm khi thời điểm này là mới. Một rebirth chỉ nhắc lại channel này: cùng cell, cùng
        // giá trị, cùng đồng hồ device, do đó cùng natural key — deduplication có quyền chỉ lưu nó
        // một lần, và đếm thêm ở đây sẽ đẩy vế trái của D1 vượt vế phải đúng một DBIRTH trọn vẹn mỗi
        // lần rebirth. Đo được chính xác -48 trên một line tám channel. Một lần nhắc lại của một
        // measurement không phải là một measurement khác.
        if (at != _lastDeclaredAt)
        {
            MeasurementCount += declared.Length;
            _lastDeclaredAt = at;
        }

        return declared;
    }

    /// <summary>Đọc channel và chỉ trả về những gì đã thay đổi kể từ lần trước.</summary>
    /// <param name="elapsed">Cell đã đi được bao xa trong cycle.</param>
    /// <param name="at">Đồng hồ device cho lần sample này.</param>
    /// <returns>Các reading đã thay đổi, mà nhiều khi chẳng có cái nào cả.</returns>
    /// <exception cref="InvalidOperationException">Channel đang trống.</exception>
    public ImmutableArray<DeviceReading> Sample(TimeSpan elapsed, DateTimeOffset at)
    {
        if (CellSerial is null)
        {
            throw new InvalidOperationException(
                $"Channel '{Path.Value}' has no cell in it, so there is nothing to measure.");
        }

        var sample = Shift(_profile.At(elapsed));
        var changed = ImmutableArray.CreateBuilder<DeviceReading>(5);

        if (Math.Abs(sample.Volts - _lastVolts) >= VoltageDeadband)
        {
            changed.Add(Reading(VoltageMetric, VoltageAlias, new MetricValue.Real(sample.Volts), at));
        }

        if (Math.Abs(sample.Amperes - _lastAmperes) >= CurrentDeadband)
        {
            changed.Add(Reading(CurrentMetric, CurrentAlias, new MetricValue.Real(sample.Amperes), at));
        }

        if (Math.Abs(sample.Celsius - _lastCelsius) >= TemperatureDeadband)
        {
            changed.Add(Reading(TemperatureMetric, TemperatureAlias, new MetricValue.Real(sample.Celsius), at));
        }

        if (Math.Abs(sample.AmpHours - _lastAmpHours) >= CapacityDeadband)
        {
            changed.Add(Reading(CapacityMetric, CapacityAlias, new MetricValue.Real(sample.AmpHours), at));
        }

        if (sample.Step != _lastStep)
        {
            changed.Add(Reading(StepMetric, StepAlias, new MetricValue.Integral((long)sample.Step), at));
        }

        // Chỉ những signal thực sự được gửi mới được ghi nhớ. Nếu ghi nhớ theo sample thay vì vậy
        // thì một giá trị có thể trôi qua khỏi deadband từng bước nhỏ dưới ngưỡng một mà không bao
        // giờ bị báo cáo — đúng kiểu bug kinh điển của report-by-exception, nơi một độ trôi chậm trở
        // nên vô hình.
        RememberSent(changed, sample);

        // Reading đã tồn tại tại thời điểm này và không thể lấy lại lần nữa: trạng thái deadband đã
        // di chuyển theo nó rồi, nên sample kế tiếp sẽ so với một giá trị mà lần này đã báo cáo. Nếu
        // hoãn việc đếm cho tới khi publish thành công thì sẽ để lại một reading mà channel thực sự
        // đã lấy nhưng không có gì mô tả được nó cả.
        MeasurementCount += changed.Count;

        return changed.DrainToImmutable();
    }

    private static DeviceReading Reading(string name, ulong alias, MetricValue value, DateTimeOffset at) =>
        new(name, alias, value, at);

    /// <summary>Một độ lệch ổn định theo từng channel, nằm trong [-1, 1].</summary>
    private static double SpreadOf(string code) => ((StableHash.Of(code) % 2001) / 1000.0) - 1.0;

    // Hai cell không bao giờ giống hệt nhau, và một line mà channel nào cũng đọc ra đúng một con số
    // là một line mà một bug gộp nhóm ở phía sau sẽ không thể nào bị phát hiện. Độ lệch này đủ nhỏ
    // để vẫn nằm trong phạm vi một cycler thật có thể chấp nhận và đủ lớn để phân biệt được các
    // channel với nhau.
    private FormationSample Shift(FormationSample sample) =>
        sample with
        {
            Volts = sample.Volts * (1 + (_spread * 0.005)),
            Celsius = sample.Celsius + (_spread * 0.8),
            AmpHours = sample.AmpHours * (1 + (_spread * 0.01)),
        };

    private void Remember(FormationSample sample)
    {
        _lastVolts = sample.Volts;
        _lastAmperes = sample.Amperes;
        _lastCelsius = sample.Celsius;
        _lastAmpHours = sample.AmpHours;
        _lastStep = sample.Step;
    }

    private void RememberSent(IEnumerable<DeviceReading> sent, FormationSample sample)
    {
        foreach (var reading in sent)
        {
            switch (reading.Alias)
            {
                case VoltageAlias:
                    _lastVolts = sample.Volts;
                    break;
                case CurrentAlias:
                    _lastAmperes = sample.Amperes;
                    break;
                case TemperatureAlias:
                    _lastCelsius = sample.Celsius;
                    break;
                case CapacityAlias:
                    _lastAmpHours = sample.AmpHours;
                    break;
                case StepAlias:
                    _lastStep = sample.Step;
                    break;
                default:
                    break;
            }
        }
    }
}
