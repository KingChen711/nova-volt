using Nvm.Simulator.Publishing;
using Nvm.Sparkplug;

namespace Nvm.Simulator.Faults;

/// <summary>Đứng giữa line và broker rồi cố tình hành xử sai.</summary>
/// <remarks>
/// <para>
/// Luôn nằm trên đường đi, kể cả khi mọi rate đều bằng 0, để run report có thể nói rõ các fault đã
/// làm gì thay vì để người đọc phải tự suy ra từ việc có ai nhớ cấu hình chúng hay không.
/// </para>
/// <para>
/// Hai fault nằm ở đây vì cả hai đều là thuộc tính của <b>đường truyền</b>, không phải của nhà máy:
/// một message bị gửi hai lần và một kết nối chập chờn. Fault thứ ba — đồng hồ sai — thuộc về device
/// và được áp dụng ngay tại nơi reading được lấy (<see cref="DeviceClockDrift"/>).
/// </para>
/// </remarks>
public sealed partial class FaultInjectingPublisher : ISparkplugPublisher
{
    private readonly ISparkplugPublisher _inner;
    private readonly SimulatorFaults _faults;
    private readonly TimeProvider _time;
    private readonly ILogger<FaultInjectingPublisher> _logger;
    private readonly Random _dice;
    private readonly Queue<SparkplugMessage> _held = new();

    private DateTimeOffset _nextDropout = DateTimeOffset.MaxValue;
    private DateTimeOffset _reconnectAt;
    private bool _offline;

    /// <summary>Bọc quanh một publisher.</summary>
    /// <param name="inner">Nơi message đi tới khi đường truyền hoạt động bình thường.</param>
    /// <param name="faults">Fault nào đang bật.</param>
    /// <param name="time">Đồng hồ mà lịch dropout chạy theo.</param>
    /// <param name="logger">Log.</param>
    public FaultInjectingPublisher(
        ISparkplugPublisher inner,
        SimulatorFaults faults,
        TimeProvider time,
        ILogger<FaultInjectingPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(faults);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        faults.Validate();

        _inner = inner;
        _faults = faults;
        _time = time;
        _logger = logger;
        _dice = new Random(faults.Seed);
    }

    /// <summary>Bao nhiêu message bị gửi lần thứ hai.</summary>
    public long DuplicateMessages { get; private set; }

    /// <summary>Bao nhiêu message publisher bên trong đã chấp nhận, kể cả bản duplicate.</summary>
    /// <remarks>
    /// Ranh giới ở đây rất chính xác và không giống với ranh giới <see cref="PublishAsync"/> trả
    /// về: một message đang bị <see cref="Held"/> qua một dropout giả lập thì chưa được publish và
    /// chưa nằm trong con số này cho tới lần flush giải phóng nó. Dưới MQTT QoS 1, publisher bên
    /// trong chỉ trả về khi broker đã acknowledge, nên qua mốc đó thì "broker đã có nó" là điều số
    /// này có quyền khẳng định.
    /// </remarks>
    public long PublishedMessages { get; private set; }

    /// <summary>Đường truyền đã rớt bao nhiêu lần.</summary>
    public long Dropouts { get; private set; }

    /// <summary>Số message chờ đường truyền quay lại nhiều nhất từng có tại một thời điểm.</summary>
    public int HeldHighWater { get; private set; }

    /// <summary>Đang có bao nhiêu message chờ ngay lúc này.</summary>
    public int Held => _held.Count;

    /// <inheritdoc />
    /// <remarks>
    /// Chuyển thẳng xuống dưới. Một rebirth request là control message không dính fault từ host;
    /// việc bỏ nó trong lúc dropout giả lập sẽ mô phỏng một đường truyền mất command nhưng không
    /// mất data, và không đường truyền thật nào hành xử như vậy.
    /// </remarks>
    public Func<CancellationToken, Task>? RebirthRequested
    {
        get => _inner.RebirthRequested;
        set => _inner.RebirthRequested = value;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Chuyển thẳng xuống dưới, cùng lý do như rebirth: mở session là việc của transport, và một
    /// fault nuốt mất nó sẽ mô phỏng một đường truyền mà không dây cáp nào có thể là.
    /// </remarks>
    public Func<ulong>? BeginSession
    {
        get => _inner.BeginSession;
        set => _inner.BeginSession = value;
    }

    /// <inheritdoc />
    public Func<CancellationToken, Task>? SessionRestored
    {
        get => _inner.SessionRestored;
        set => _inner.SessionRestored = value;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);

        ScheduleNextDropout(_time.GetUtcNow());
    }

    /// <inheritdoc />
    /// <remarks>
    /// Trả về mà không throw <b>không</b> có nghĩa là broker đã nhận được message. Trong lúc đường
    /// truyền rớt, hàm này giữ message trong bộ nhớ rồi trả về, đúng như cách một device có buffer
    /// nội bộ nhỏ hành xử, và message được gửi đi ở lần flush ngay sau khi kết nối lại. Nếu caller
    /// coi việc trả về thành công là đã delivery thì sẽ đếm nhầm những message vẫn còn nằm trên
    /// device — đó là lý do run report đếm measurement tại nơi channel lấy chúng và để việc
    /// delivery lại cho <see cref="PublishedMessages"/>.
    /// </remarks>
    public async Task PublishAsync(SparkplugMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var now = _time.GetUtcNow();

        if (_offline)
        {
            if (now < _reconnectAt)
            {
                Hold(message);
                return;
            }

            _offline = false;
            ScheduleNextDropout(now);
            LinkRestored(_logger, _held.Count);

            // Toàn bộ backlog cùng một lúc, đây là chủ đích của fault chứ không phải tình cờ do
            // cách viết code. Một đường truyền quay lại thì không nhỏ giọt: mọi gateway trong khu
            // vực kết nối lại trong cùng một giây và đổ hết vào một ingestion service vừa mới khởi
            // động lại. Cú dồn đó chính là thứ rate limit của C10 tồn tại để chịu được, và một fault
            // thả backlog ra từ từ thì sẽ chẳng còn gì để chứng minh điều đó.
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (now >= _nextDropout)
        {
            _offline = true;
            _reconnectAt = now + _faults.DropoutDuration;
            Dropouts++;
            LinkLost(_logger, _faults.DropoutDuration);
            Hold(message);
            return;
        }

        await SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Chuyển thẳng xuống dưới. Có session hay không là việc của transport, và một fault tự trả lời
    /// câu hỏi này sẽ để một run publish vào một đường truyền mà class này chỉ đang giả vờ là có.
    /// </remarks>
    public Task WaitForSessionAsync(CancellationToken cancellationToken) =>
        _inner.WaitForSessionAsync(cancellationToken);

    /// <summary>Gửi mọi thứ đang bị giữ, bất kể đường truyền đã tới hạn quay lại hay chưa.</summary>
    /// <param name="cancellationToken">Hủy lần flush.</param>
    /// <remarks>
    /// Được gọi ở cuối một run. Một dropout là một <b>khoảng trống</b>, không phải một mất mát —
    /// message vẫn nằm trên device và device sẽ gửi chúng — nên một run kết thúc giữa lúc dropout
    /// mà bỏ luôn backlog sẽ báo cáo số measurement đã lấy nhiều hơn số thực sự từng được gửi ra, và
    /// D1 lúc đó sẽ đo cái shutdown chứ không phải cái pipeline.
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (_held.Count > 0)
        {
            await SendAsync(_held.Dequeue(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Publisher bên trong thuộc quyền sở hữu của bên đã tạo ra nó, không phải của wrapper này, nên
    /// nó không bị dispose ở đây. Bất cứ thứ gì còn đang bị giữ là việc của caller phải flush — xem
    /// <see cref="FlushAsync"/>, nơi worker gọi tới, chỗ mà thứ tự so với shutdown của chính broker
    /// có thể quan sát được.
    /// </remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Source-generated vì lý do CA1873 đưa ra: một tham số TimeSpan sẽ bị box, và đường code này
    // chạy hàng nghìn lần mỗi giây.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Link lost, holding messages for {Duration}")]
    private static partial void LinkLost(ILogger logger, TimeSpan duration);

    [LoggerMessage(Level = LogLevel.Information, Message = "Link restored, releasing {Held} held messages")]
    private static partial void LinkRestored(ILogger logger, int held);

    private async Task SendAsync(SparkplugMessage message, CancellationToken cancellationToken)
    {
        await _inner.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        PublishedMessages++;

        if (_faults.DuplicateRate <= 0 || _dice.NextDouble() >= _faults.DuplicateRate)
        {
            return;
        }

        // Cùng một object, được gửi lại lần nữa. Không phải một message thứ hai được dựng từ cùng
        // các reading đó: một message như vậy sẽ mang một seq mới và, nếu đồng hồ đã trôi, một
        // device_timestamp mới — và một reading tại một thời điểm mới là một measurement khác, mà
        // deduplication có quyền giữ lại. Khi đó phép đối chiếu sẽ ra khớp trong khi chưa hề
        // deduplicate được gì cả, và D1 sẽ báo cáo thành công cho một test chưa chạy gì hết. Đây là
        // R-M2-1.
        await _inner.PublishAsync(message, cancellationToken).ConfigureAwait(false);

        DuplicateMessages++;
        PublishedMessages++;
    }

    private void Hold(SparkplugMessage message)
    {
        _held.Enqueue(message);

        if (_held.Count > HeldHighWater)
        {
            HeldHighWater = _held.Count;
        }
    }

    private void ScheduleNextDropout(DateTimeOffset now)
    {
        if (_faults.DropoutMeanInterval <= TimeSpan.Zero)
        {
            _nextDropout = DateTimeOffset.MaxValue;
            return;
        }

        // Khoảng cách theo phân phối mũ (exponential), đây là kết quả của một Poisson process và
        // cũng là hình dạng của một đường truyền chập chờn. 1 - NextDouble() rơi vào (0, 1] nên
        // logarithm luôn xác định; giá trị clamp giữ cho một lần rút số quá xui không lên lịch
        // dropout kế tiếp vượt quá tận cùng thời gian.
        var mean = _faults.DropoutMeanInterval.TotalSeconds;
        var gap = Math.Min(-Math.Log(1 - _dice.NextDouble()) * mean, mean * 100);

        _nextDropout = now + TimeSpan.FromSeconds(gap);
    }
}
