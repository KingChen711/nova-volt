using System.Collections.Immutable;
using System.Globalization;
using Nvm.Kernel.Identity;
using Nvm.Simulator.Faults;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.Simulator.Formation;

/// <summary>Một formation line giả lập một edge node với một cycler bên dưới nó.</summary>
/// <remarks>
/// <para>
/// Mọi thứ ở đây đều là hàm của <b>process time</b> — nhà máy đã tiến được bao xa — chứ không bao giờ
/// là hàm của wall clock. Đó chính là điều làm cho time compression trung thực: chạy một cycle nhanh
/// gấp nghìn lần chỉ thay đổi thời gian chạy hết một run, còn các measurement tạo ra thì giống hệt
/// nhau, vì các sample luôn được lấy ở những điểm cố định của process time.
/// </para>
/// <para>
/// Sequence number thuộc về đây chứ không thuộc về một channel. <c>seq</c> của Sparkplug là theo từng
/// edge node và cuộn lại ở 256, và đó là cách một consumer nhận ra nó đã bỏ lỡ một message — mỗi
/// device có một counter riêng sẽ khiến một khoảng hở trong đó trở nên vô nghĩa.
/// </para>
/// <para>
/// Các channel được so le nhau trên cycle thay vì bắt đầu cùng lúc. Một line thật nạp cell liên tục,
/// nên ở bất kỳ thời điểm nào cũng có channel đang charging, có channel đang nghỉ và có channel đang
/// discharging — một line mà cả nghìn channel chuyển stage cùng một khoảnh khắc sẽ tạo ra một hình
/// dạng traffic mà downstream sẽ không bao giờ gặp lại lần nữa.
/// </para>
/// </remarks>
public sealed class FormationLine
{
    /// <summary>Sparkplug cuộn <c>seq</c> tại ngưỡng này, và một consumer trông cậy vào việc cuộn đó.</summary>
    private const ulong SequenceWrap = 256;

    private const string BirthDeathSequenceMetric = "bdSeq";
    private const string RebirthControlMetric = "Node Control/Rebirth";

    private readonly FormationProfile _profile;
    private readonly ImmutableArray<FormationChannel> _channels;
    private readonly long[] _cycleNumbers;
    private readonly TimeSpan[] _driftOffsets;
    private readonly DateTimeOffset _startedAt;

    // Interlocked, và là long thay vì ulong để làm được điều đó. Một MQTT session mới được mở bởi
    // reconnect loop của publisher, đây là một thread khác với tick loop soạn message dưới session
    // này - nên con số này được thay đổi trên một thread và được đọc trên thread khác. Nó không
    // thể được lấy dưới publishing lock: reconnect loop gọi BeginSession trước khi socket được mở,
    // và tick loop có thể đang giữ lock đó trong khi chờ chính socket ấy.
    private long _birthDeathSequence;
    private readonly DeviceClockDrift _drift;

    private ulong _sequence;

    /// <summary>Tạo line và các channel bên dưới nó.</summary>
    /// <param name="linePath">Line, tức là thứ mà edge node đại diện phát ngôn.</param>
    /// <param name="channelPaths">Các channel mà nhà máy thực sự có, lấy từ factory model.</param>
    /// <param name="profile">Hình dạng cycle.</param>
    /// <param name="startedAt">Đồng hồ device tại thời điểm process time bằng không.</param>
    /// <param name="birthDeathSequence">Session number mà run này publish dưới đó.</param>
    /// <param name="drift">Channel nào có đồng hồ sai. Null nghĩa là mọi đồng hồ đều đúng.</param>
    /// <exception cref="ArgumentException">Path không phải một line, hoặc không có channel nào.</exception>
    public FormationLine(
        EquipmentPath linePath,
        IEnumerable<EquipmentPath> channelPaths,
        FormationProfile profile,
        DateTimeOffset startedAt,
        ulong birthDeathSequence = 0,
        DeviceClockDrift? drift = null)
    {
        ArgumentNullException.ThrowIfNull(linePath);
        ArgumentNullException.ThrowIfNull(channelPaths);
        ArgumentNullException.ThrowIfNull(profile);

        if (linePath.Kind != FactoryNodeKind.Line)
        {
            throw new ArgumentException(
                $"'{linePath.Value}' is a {linePath.Kind}; an edge node speaks for a line.",
                nameof(linePath));
        }

        _channels = [.. channelPaths.Select(path => new FormationChannel(path, profile))];

        if (_channels.IsEmpty)
        {
            throw new ArgumentException(
                $"'{linePath.Value}' has no channels to simulate. The plant's model is the source of "
                + "that list, so an empty one means the line was decommissioned or never rolled out.",
                nameof(channelPaths));
        }

        Path = linePath;
        _profile = profile;
        _startedAt = startedAt;
        _birthDeathSequence = (long)birthDeathSequence;
        _drift = drift ?? DeviceClockDrift.None;
        _cycleNumbers = new long[_channels.Length];
        _driftOffsets = new TimeSpan[_channels.Length];

        for (var index = 0; index < _channels.Length; index++)
        {
            _cycleNumbers[index] = -1;
            _driftOffsets[index] = _drift.For(_channels[index].Path.Code);
        }

        DriftedDeviceCount = _driftOffsets.Count(offset => offset != TimeSpan.Zero);
    }

    /// <summary>Line mà node này đại diện phát ngôn.</summary>
    public EquipmentPath Path { get; }

    /// <summary>Có bao nhiêu channel bên dưới nó.</summary>
    public int ChannelCount => _channels.Length;

    /// <summary>Mọi measurement mà line đã LẤY (TAKEN). Vế bên trái của reconciliation ở D1.</summary>
    /// <remarks>
    /// <para>
    /// Không bị ảnh hưởng bởi bất kỳ fault nào trong hai loại, và đó chính là mục đích. Một duplicate
    /// là cùng một measurement được gửi hai lần, còn một đồng hồ drift là cùng một measurement bị
    /// đóng dấu sai — không cái nào trong hai cái là một reading mà channel thực sự lấy, nên không
    /// cái nào thuộc về vế này của reconciliation.
    /// </para>
    /// <para>
    /// Nó thay đổi trong <see cref="Connect"/> và <see cref="Advance"/>, nơi các channel được đọc,
    /// và không bao giờ thay đổi sau đó. Vế này của reconciliation mô tả <b>nhà máy</b>; vế bên phải
    /// mô tả database. Nếu dời số này xuống sau transport thì cả hai vế sẽ cùng nằm sau cùng một chỗ
    /// mất mát, và phép so sánh bằng sẽ đúng trên dữ liệu chưa từng đến nơi.
    /// </para>
    /// </remarks>
    public long MeasurementCount => _channels.Sum(channel => channel.MeasurementCount);

    /// <summary>Có bao nhiêu channel đang chạy trên một đồng hồ sai.</summary>
    public int DriftedDeviceCount { get; }

    /// <summary>Node lên online: một <c>NBIRTH</c>, rồi một <c>DBIRTH</c> cho mỗi channel.</summary>
    /// <param name="processElapsed">Nhà máy đã tiến được bao xa khi node kết nối.</param>
    /// <remarks>
    /// Thứ tự này không phải là trang trí. Một consumer thấy <c>DBIRTH</c> trước birth của chính node
    /// sẽ có một device thuộc về một session mà nó chưa từng nghe tới, đây chính là trạng thái mà C11
    /// yêu cầu rebirth để xử lý.
    /// </remarks>
    public ImmutableArray<ComposedMessage> Connect(TimeSpan processElapsed)
    {
        var nodeClock = _startedAt + processElapsed;
        var messages = ImmutableArray.CreateBuilder<ComposedMessage>(_channels.Length + 1);

        // seq bắt đầu lại từ 0 khi có một birth, đó chính là cách báo cho consumer biết rằng số đếm
        // nó đang giữ thuộc về một session đã kết thúc.
        _sequence = 0;

        // Đồng hồ của chính node, không bao giờ là một đồng hồ bị drift. Drift thuộc về mặt điều
        // khiển (front panel) của một device, còn edge node là một hộp khác; đóng dấu node birth
        // bằng lỗi của device sẽ khiến chính bdSeq đến từ một giờ sai và làm hỏng việc khớp session
        // của C11.
        messages.Add(new ComposedMessage(
            Message(
                SparkplugMessageType.NodeBirth,
                Path,
                SparkplugPayload.EncodeBirth(NodeBirthMetrics(nodeClock), _sequence, nodeClock)),
            // Không có gì ở cả hai vế của reconciliation: một NBIRTH mang bdSeq và metric điều khiển
            // rebirth, gateway không forward cái nào trong hai cái, và cũng không thiết bị nào đo
            // chúng.
            Measurements: 0));

        for (var index = 0; index < _channels.Length; index++)
        {
            messages.Add(DeclareChannel(index, processElapsed, nodeClock));
        }

        return messages.DrainToImmutable();
    }

    /// <summary>Bắt đầu một MQTT session mới và trả về <c>bdSeq</c> định danh nó.</summary>
    /// <remarks>
    /// Chỉ một connection mới mới được gọi cái này. Một rebirth thì KHÔNG được: một rebirth khai báo
    /// lại node bên trong session mà nó đang ở, và việc thay đổi <c>bdSeq</c> ở đó sẽ báo cho gateway
    /// rằng session đã bị thay thế. Gateway hành động chính xác dựa trên phép so sánh đó - nó bỏ qua
    /// một NDEATH có <c>bdSeq</c> chỉ tên một session đã kết thúc rồi - nên con số này phải thay đổi
    /// đúng một lần cho mỗi connection và không bao giờ khác đi, nếu không một will đến trễ sẽ giết
    /// chết session đang sống.
    /// </remarks>
    public ulong BeginSession() => (ulong)Interlocked.Increment(ref _birthDeathSequence);

    /// <summary>Session mà line này đang publish dưới đó.</summary>
    public ulong BirthDeathSequence => (ulong)Interlocked.Read(ref _birthDeathSequence);

    /// <summary>Đưa nhà máy tới một thời điểm và trả về những gì line publish tại đó.</summary>
    /// <param name="processElapsed">Nhà máy đã tiến được bao xa.</param>
    /// <returns>
    /// Một <c>DBIRTH</c> cho mỗi channel vừa nhận một cell mới, và một <c>DDATA</c> cho mỗi channel
    /// có reading thay đổi. Thường ít hơn nhiều so với một message trên mỗi channel — đó là
    /// report-by-exception đang làm đúng việc của nó.
    /// </returns>
    public ImmutableArray<ComposedMessage> Advance(TimeSpan processElapsed)
    {
        var nodeClock = _startedAt + processElapsed;
        var messages = ImmutableArray.CreateBuilder<ComposedMessage>();

        for (var index = 0; index < _channels.Length; index++)
        {
            if (CycleNumberAt(index, processElapsed) != _cycleNumbers[index])
            {
                messages.Add(DeclareChannel(index, processElapsed, nodeClock));
                continue;
            }

            var deviceClock = nodeClock + _driftOffsets[index];
            var changed = _channels[index].Sample(CycleElapsedAt(index, processElapsed), deviceClock);

            if (!changed.IsEmpty)
            {
                messages.Add(new ComposedMessage(
                    Message(
                        SparkplugMessageType.DeviceData,
                        _channels[index].Path,
                        SparkplugPayload.EncodeData(changed, NextSequence(), deviceClock)),
                    changed.Length));
            }
        }

        return messages.DrainToImmutable();
    }

    private static SparkplugMessage Message(SparkplugMessageType messageType, EquipmentPath path, byte[] payload) =>
        new(SparkplugTopic.For(path, messageType), [.. payload]);

    private ImmutableArray<DeviceReading> NodeBirthMetrics(DateTimeOffset deviceClock) =>
    [
        // bdSeq không mang alias, theo đúng specification: nó phải đọc được trong một NDEATH mà
        // broker publish dưới dạng last will, và một last will được soạn trước cả birth — thứ lẽ ra
        // đã gán alias.
        new DeviceReading(BirthDeathSequenceMetric, null, new MetricValue.Integral(Interlocked.Read(ref _birthDeathSequence)), deviceClock),
        new DeviceReading(RebirthControlMetric, null, new MetricValue.Flag(false), deviceClock),
    ];

    private ComposedMessage DeclareChannel(int index, TimeSpan processElapsed, DateTimeOffset nodeClock)
    {
        var cycleNumber = CycleNumberAt(index, processElapsed);

        _cycleNumbers[index] = cycleNumber;

        // Serial đọc đồng hồ của node còn các reading đọc đồng hồ của device. Một đồng hồ front-panel
        // sai không khắc lại serial của cell đang nằm trong channel, và một fault làm thay đổi serial
        // sẽ là mô phỏng một cell bị gắn nhãn sai chứ không phải một cell đến trễ — một lỗi khác hẳn,
        // ồn ào hơn nhiều, và không phải loại mà C13 phải phân loại.
        var deviceClock = nodeClock + _driftOffsets[index];

        // Đọc chênh lệch qua declaration thay vì lấy từ mảng nó trả về. Một DBIRTH khai báo sáu
        // reading, nhưng một REBIRTH khai báo lại đúng sáu reading đó tại cùng một khoảnh khắc — cùng
        // natural key, mà database chỉ lưu một lần, nên nó không cộng thêm gì vào vế bên trái. Channel
        // là nơi duy nhất biết đây là cái nào trong hai loại, và chênh lệch chính là cách nó nói lên
        // điều đó. Nếu lấy readings.Length ở đây thay vào thì một rebirth bị bỏ dở sẽ trông như sáu
        // measurement bị mất trong khi thực ra chúng chưa từng mất.
        var counted = _channels[index].MeasurementCount;

        var readings = _channels[index].Declare(
            CellSerialFor(index, cycleNumber, nodeClock),
            CycleElapsedAt(index, processElapsed),
            deviceClock);

        return new ComposedMessage(
            Message(
                SparkplugMessageType.DeviceBirth,
                _channels[index].Path,
                SparkplugPayload.EncodeBirth(readings, NextSequence(), deviceClock)),
            (int)(_channels[index].MeasurementCount - counted));
    }

    private TimeSpan Offset(int index) => _profile.CycleDuration * index / _channels.Length;

    private TimeSpan CycleElapsedAt(int index, TimeSpan processElapsed)
    {
        var total = processElapsed + Offset(index);

        return total - (_profile.CycleDuration * Math.Floor(total / _profile.CycleDuration));
    }

    private long CycleNumberAt(int index, TimeSpan processElapsed) =>
        (long)Math.Floor((processElapsed + Offset(index)) / _profile.CycleDuration);

    /// <summary>Dựng serial 16 ký tự mà cell trong một channel đã được khắc lên.</summary>
    /// <remarks>
    /// Layout lấy từ docs/scope.md §6.1, và được kiểm tra đối chiếu với <see cref="SerialNumber"/>
    /// thay vì tin tưởng suông: một simulator âm thầm tạo ra serial sai định dạng sẽ khiến mọi
    /// genealogy test ở downstream pass trên dữ liệu mà không engraver nào có thể tạo ra được.
    /// <para>
    /// Chữ cái shift lấy từ giờ UTC. Ngày sản xuất và shift là lịch riêng của nhà máy và thuộc về M3;
    /// dùng giờ trần như ở đây chỉ là một placeholder sai theo một cách đã được nêu rõ, chứ không sai
    /// theo kiểu ẩn giấu.
    /// </para>
    /// <para>
    /// Một hàm thuần (pure function) của channel và cycle, không phải một counter. Một node reconnect
    /// sẽ republish một <c>DBIRTH</c> cho một cell đã có sẵn trong channel, và một counter sẽ gán cho
    /// đúng cell đó một identity thứ hai — điều duy nhất mà một serial number không được phép làm.
    /// </para>
    /// </remarks>
    private string CellSerialFor(int index, long cycleNumber, DateTimeOffset deviceClock)
    {
        // Cycle là phần cao và channel là phần thấp, nên cả nghìn cell được nạp trên toàn line trong
        // một lượt sẽ nhận một nghìn số liên tiếp, đúng như cách nhà máy cấp số cho chúng. Khoảng giá
        // trị là 1-99999 vì một serial không có cell số 0: một engraver bắt đầu đếm một shift từ một.
        var sequence = (((cycleNumber * _channels.Length) + index) % 99_999) + 1;

        var shift = deviceClock.Hour switch
        {
            >= 6 and < 14 => 'A',
            >= 14 and < 22 => 'B',
            _ => 'C',
        };

        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{Path.SiteId}C{Path.Code}{deviceClock.Year % 10}{deviceClock.DayOfYear:000}{shift}{sequence:00000}");

        return SerialNumber.TryParse(value, out var serial)
            ? serial.Value
            : throw new InvalidOperationException(
                $"The simulator built '{value}', which is not a serial number this plant could engrave.");
    }

    private ulong NextSequence()
    {
        _sequence = (_sequence + 1) % SequenceWrap;

        return _sequence;
    }
}
