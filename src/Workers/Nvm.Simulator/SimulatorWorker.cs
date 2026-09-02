using System.Collections.Immutable;
using Nvm.Simulator.Faults;
using Nvm.Simulator.Formation;
using Nvm.Simulator.Publishing;
using Nvm.Simulator.Reporting;

namespace Nvm.Simulator;

/// <summary>Chạy nhà máy: một tick wall clock, một sample period của process time.</summary>
/// <remarks>
/// <para>
/// Vòng lặp này chính là toàn bộ luận điểm về time compression. Process time luôn tiến thêm đúng
/// <see cref="SimulatorOptions.SamplePeriod"/>, nên các sample rơi vào đúng những điểm giống nhau của
/// cycle bất kể run chạy nhanh tới đâu; compression chỉ quyết định tick chờ bao lâu. Tăng compression
/// lên thì run kết thúc sớm hơn với một tập measurement giống hệt.
/// </para>
/// <para>
/// Mọi lần chờ đều đi qua <see cref="TimeProvider"/> (K1). Đây không phải là hình thức ở đây — chính
/// điều này cho phép một test chạy hết một cycle mười tám giờ trong vài mili-giây và đếm những gì đã
/// ra.
/// </para>
/// </remarks>
public sealed partial class SimulatorWorker : BackgroundService
{
    /// <summary>Khoảng cách ngắn nhất giữa hai lần trả lời yêu cầu rebirth.</summary>
    /// <remarks>
    /// Một consumer bỏ lỡ các birth sẽ hỏi một lần cho mỗi gap nó phát hiện, nên nó hỏi tới hàng nghìn
    /// lần trước khi câu trả lời đầu tiên tới được nó. Trả lời từng cái một sẽ republish mọi DBIRTH
    /// trên line hàng nghìn lần và nhấn chìm chính dữ liệu mà consumer đó đang cố đọc.
    /// </remarks>
    private static readonly TimeSpan RebirthCooldown = TimeSpan.FromSeconds(5);

    private readonly FormationLine _line;
    private readonly FaultInjectingPublisher _publisher;
    private readonly SimulatorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SimulatorWorker> _logger;
    private readonly SemaphoreSlim _publishing = new(1, 1);

    private DateTimeOffset _lastRebirth = DateTimeOffset.MinValue;
    private long _rebirths;

    // Node này đã tự khai báo dưới session nào. Null cho tới lần khai báo đầu tiên.
    //
    // Một session được mở bởi transport; thứ khiến nó DÙNG ĐƯỢC là việc khai báo. Mọi consumer đọc
    // DDATA dựa trên một alias table mà một DBIRTH đã cấp cho nó, nên một DDATA được publish dưới một
    // session mà các birth của nó chưa ra ngoài là một message không ai giải mã được - và không có
    // cách nào khôi phục lại sau đó, vì một alias là một con số không gắn với tên nào cả.
    //
    // Chỉ được đọc và ghi dưới _publishing, đó chính là điều biến "khai báo trước bất kỳ dữ liệu nào"
    // thành một quy tắc thay vì một race giữa tick loop và bất kỳ MQTT thread nào phát hiện reconnect.
    private ulong? _declaredSession;

    /// <summary>Tạo worker.</summary>
    /// <param name="line">Line cần chạy.</param>
    /// <param name="publisher">Message đi đâu, và điều gì có thể sai trên đường đi.</param>
    /// <param name="options">Nhanh cỡ nào, và thường xuyên cỡ nào.</param>
    /// <param name="time">Đồng hồ. Các run bị nén vẫn đi qua nó.</param>
    /// <param name="logger">Log.</param>
    public SimulatorWorker(
        FormationLine line,
        FaultInjectingPublisher publisher,
        SimulatorOptions options,
        TimeProvider time,
        ILogger<SimulatorWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _line = line;
        _publisher = publisher;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Có bao nhiêu message line đã soạn, birth và data gộp lại.</summary>
    /// <remarks>
    /// Những gì nhà máy định nói. Những gì broker thực sự nghe được thì lớn hơn tùy theo có bao nhiêu
    /// duplicate đã bị bơm vào, và nhỏ hơn tùy theo đường truyền đã từ chối bao nhiêu, và cả hai con
    /// số đó nằm ở publisher — xem <see cref="Faults.FaultInjectingPublisher.PublishedMessages"/>.
    /// </remarks>
    public long LogicalMessageCount { get; private set; }

    /// <summary>Measurement mà nhà máy đã lấy nhưng đường truyền chưa từng chuyển đi.</summary>
    /// <remarks>
    /// <para>
    /// Một batch được soạn dưới một session và được publish từng message một, và có hai thứ vẫn có
    /// thể cắt ngang nó: session kết thúc giữa hai message, và một publish ném exception. Phần còn lại
    /// đã được đo và sẽ không bao giờ được gửi — giống như một device không có buffer bị mất dữ liệu
    /// đang truyền dở khi cáp của nó bị rút, đây là một điều có thật mà simulator này có thể làm.
    /// </para>
    /// <para>
    /// Đây là một <b>chẩn đoán</b>, không bao giờ là một điều chỉnh. Những reading đó vẫn ở trong
    /// <see cref="Formation.FormationLine.MeasurementCount"/>, nên D1 và D3 sẽ đỏ vì chúng; bỏ chúng
    /// ra khỏi vế bên trái chính là cách chắc chắn nhất khiến một oracle không còn thấy được một lần
    /// mất mát. Counter này chỉ nói lên mất mát xảy ra ở phía nào của đường truyền, và D1 cùng D3 đều
    /// yêu cầu nó phải bằng không.
    /// </para>
    /// </remarks>
    public long AbandonedMeasurements { get; private set; }

    /// <summary>Nhà máy đã tiến được bao xa.</summary>
    public TimeSpan ProcessElapsed { get; private set; }

    /// <summary>Node này đã tự khai báo lại chính nó bao nhiêu lần vì một consumer yêu cầu.</summary>
    public long Rebirths => Interlocked.Read(ref _rebirths);

    /// <summary>True kể từ khi line online và tick loop đã được bật.</summary>
    /// <remarks>
    /// Nhà máy chưa chạy cho tới khi timer của nó tồn tại, và trước đó một tick đến là một tick không
    /// ai giữ. Thời gian thực không bao giờ tạo ra một tick sớm như vậy; một test điều khiển một đồng
    /// hồ giả thì có thể, nên property này nói rõ khi nào câu trả lời cho "đã bắt đầu chưa" là có thay
    /// vì để phải đoán.
    /// </remarks>
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public override void Dispose()
    {
        _publishing.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();

        // Set trước khi connect, để một yêu cầu đến ngay cùng lúc với subscription đầu tiên được trả
        // lời thay vì bị rơi mất.
        _publisher.RebirthRequested = RepublishBirthsAsync;

        // Một reconnect khai báo lại node vì cùng lý do một rebirth cũng làm vậy - mọi consumer đang
        // giữ alias table của một session không còn tồn tại nữa - nhưng nó còn cần một bdSeq mới, thứ
        // mà một rebirth không bao giờ được lấy.
        _publisher.BeginSession = _line.BeginSession;
        _publisher.SessionRestored = RepublishAfterReconnectAsync;

        await _publisher.ConnectAsync(stoppingToken).ConfigureAwait(false);

        LineOnline(
            _logger,
            _line.Path.Value,
            _line.ChannelCount,
            _options.SamplePeriod,
            _options.TimeCompression);

        await DeclareAsync(stoppingToken).ConfigureAwait(false);

        // Một timer được bật một lần, không phải một delay được bật lại mỗi vòng. Có hai lý do, và lý
        // do thứ hai mới là cái quan trọng: một delay khởi động lại sau khi công việc đã xong sẽ khiến
        // period thực tế bằng interval cộng thêm thời gian publish tốn, nên tốc độ message trôi xuống
        // dưới mức mà setting tuyên bố — và các con số load ở D2 sẽ đo một nhà máy chậm hơn nhà máy
        // trên giấy. Lý do thứ nhất là giữa hai delay có một khoảnh khắc không có timer nào cả, và
        // một tick đến đúng khoảnh khắc đó là một tick bị mất.
        using var ticker = new PeriodicTimer(_options.TickInterval, _time);

        // Được ghi một lần trước vòng lặp, và lần ghi này được phép ném exception. Một report path
        // không ghi được đáng để fail ngay bây giờ hơn là một giờ sau, khi run đã kết thúc và con số
        // lẽ ra phải chứng minh D1 hóa ra chưa từng được ghi lại.
        RunReportFile.Write(_options.ReportPath, Report());

        var lastReport = _time.GetUtcNow();

        IsRunning = true;

        try
        {
            while (await ticker.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await AdvanceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (SparkplugPublishException exception)
                {
                    // Đường truyền chết giữa session gate và gói tin. Nhà máy không dừng vì điều đó
                    // (N15): tick kế tiếp chờ session kế tiếp rồi tiếp tục. Cái gì đang truyền dở thì
                    // mất, giống như một device không có buffer mất dữ liệu khi cáp của nó bị rút -
                    // simulator không giả vờ đã giữ được nó, và cũng không giả vờ chưa đo được nó. Xem
                    // AbandonedMeasurements.
                    PublishFailed(_logger, exception, _line.Path.Value);
                }

                var now = _time.GetUtcNow();

                if (now - lastReport >= _options.ReportInterval)
                {
                    lastReport = now;
                    TryWriteReport();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown, không phải một fault. Nhà máy dừng lại là kết thúc bình thường của một run.
        }

        IsRunning = false;

        // CancellationToken.None: stoppingToken đã bị hủy vào lúc thực thi tới đây, và một dropout
        // đang diễn ra là một gap chứ không phải một mất mát — các message vẫn đang ở trên device và
        // device sẽ gửi chúng. Các reading chúng mang theo đã được đếm rồi, nên truyền vào token đã bị
        // hủy sẽ khiến chúng bị kẹt lại và kết thúc run với việc báo cáo các measurement mà broker
        // chưa từng được đề nghị nhận.
        try
        {
            await _publisher.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SparkplugPublishException exception)
        {
            // Một run kết thúc trong khi broker không thể liên lạc được vẫn cần một report để ghi, và
            // report đó chính là vế bên trái của D1. Để mất nó vì một lần flush thất bại sẽ là vứt bỏ
            // con số measurement chỉ để than phiền về đường truyền.
            PublishFailed(_logger, exception, _line.Path.Value);
        }

        TryWriteReport();

        LineStopped(
            _logger,
            _line.Path.Value,
            ProcessElapsed,
            LogicalMessageCount,
            _line.MeasurementCount);
    }

    // Được source-generate vì lý do CA1873 đưa ra: một argument TimeSpan sẽ bị box, và một dòng log
    // cấp phát bộ nhớ dù có ai đang lắng nghe hay không là một chi phí phải trả trên mỗi run.
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Line {Line} online: {Channels} channels, one sample per {Sample} of plant time, {Compression}x")]
    private static partial void LineOnline(ILogger logger, string line, int channels, TimeSpan sample, double compression);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Line {Line} stopped after {Elapsed} of plant time: {Messages} messages, {Measurements} measurements")]
    private static partial void LineStopped(ILogger logger, string line, TimeSpan elapsed, long messages, long measurements);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Could not write the run report to {Path}. Measurements so far: {Measurements}, duplicates: {Duplicates}")]
    private static partial void ReportNotWritten(ILogger logger, Exception error, string path, long measurements, long duplicates);

    private RunReport Report() =>
        new(
            _line.Path.Value,
            _time.GetUtcNow(),
            ProcessElapsed,
            _line.ChannelCount,
            _line.MeasurementCount,
            AbandonedMeasurements,
            LogicalMessageCount,
            _publisher.DuplicateMessages,
            _publisher.PublishedMessages,
            _line.DriftedDeviceCount,
            _publisher.Dropouts,
            _publisher.HeldHighWater,
            _options.Faults.AnyEnabled);

    // Mọi lần ghi sau lần đầu tiên đều là best-effort, và các con số đi vào dòng log khi nó thất bại.
    // Một trục trặc ổ đĩa ở phút thứ bốn mươi của một run một giờ không nên vứt bỏ cả run, và con số
    // này là thứ reconciliation cần — file chỉ là cách thông thường nó di chuyển tới đó.
    private void TryWriteReport()
    {
        var report = Report();

        try
        {
            RunReportFile.Write(_options.ReportPath, report);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportNotWritten(_logger, error, _options.ReportPath, report.LogicalMeasurements, report.DuplicateMessages);
        }
    }

    // Republish giống hệt một rebirth, chỉ trừ cooldown. Cooldown tồn tại để hấp thụ việc một host hỏi
    // đi hỏi lại; một reconnect không phải là một yêu cầu và chỉ xảy ra một lần cho mỗi connection bị
    // rớt, nên giới hạn tốc độ nó sẽ khiến node im lặng đúng vào trường hợp nó không được phép im
    // lặng.
    private Task RepublishAfterReconnectAsync(CancellationToken cancellationToken) =>
        DeclareAsync(cancellationToken);

    // Khai báo node khi session mà nó đang publish dưới đó chưa được khai báo.
    //
    // Được gọi từ hai nơi không được phép mâu thuẫn nhau: reconnect handler, để một đường truyền vừa
    // phục hồi dùng được ngay, và đầu mỗi tick, để điều đó vẫn đúng nếu handler đó chưa từng chạy hoặc
    // chạy nửa chừng thì thất bại. Có tính idempotent theo cấu trúc - caller thứ hai thấy session đã
    // được khai báo rồi và không làm gì cả.
    private async Task DeclareAsync(CancellationToken cancellationToken)
    {
        await _publishing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await DeclareLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _publishing.Release();
        }
    }

    // Một tick của nhà máy, được soạn và publish mà không buông lock ra ở giữa chừng.
    //
    // Soạn ở ngoài lock là một lỗi thật chứ không phải lý thuyết suông: Advance thay đổi sequence
    // counter và deadband state của mọi channel, Connect reset cả hai, còn rebirth và reconnect
    // handler thì đến trên các MQTT thread. Hai cái chạy cùng lúc sẽ đan xen luồng seq của session này
    // với session khác, và một consumer đang đếm seq sẽ thấy những gap chưa từng xảy ra - rồi yêu cầu
    // rebirth cho từng cái một.
    private async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        // Được chờ ở NGOÀI lock, và cách đặt đó chính là toàn bộ thiết kế. Chặn ở đây trong khi vẫn
        // giữ lock sẽ giữ luôn cái lock mà node cần để tự khai báo trên session đang được chờ - tick
        // sẽ chờ một điều không thể xảy ra cho tới khi chính tick đó buông lock ra. Ở trên lock, một
        // đường truyền chết chỉ đơn giản là tạm dừng nhà máy cho tới khi đường truyền quay lại.
        await _publisher.WaitForSessionAsync(cancellationToken).ConfigureAwait(false);

        await _publishing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await DeclareLockedAsync().ConfigureAwait(false);

            // Không truyền token từ đây trở xuống, và đó là quy tắc shutdown chứ không phải một sơ
            // suất. Advance ĐỌC các channel: đến lúc nó trả về thì các deadband đã thay đổi và nhà máy
            // đã đo xong. Một lần dừng làm mất phần còn lại của batch sẽ để những reading đó ở vế bên
            // trái của D1 mà không có gì từng đến được vế bên phải. Một tick đã soạn xong thì hoàn
            // tất; vòng lặp dừng lại trước tick TIẾP THEO, nơi nhà máy chưa đo gì cả và việc dừng lại
            // không tốn gì.
            ProcessElapsed += _options.SamplePeriod;

            await PublishLockedAsync(_line.Advance(ProcessElapsed)).ConfigureAwait(false);
        }
        finally
        {
            _publishing.Release();
        }
    }

    private async Task DeclareLockedAsync()
    {
        var session = _line.BirthDeathSequence;

        if (_declaredSession == session)
        {
            return;
        }

        await PublishLockedAsync(_line.Connect(ProcessElapsed)).ConfigureAwait(false);

        // Set sau cùng, để một lần khai báo thất bại giữa chừng sẽ được thử lại ở tick kế tiếp thay vì
        // bị nhớ nhầm là đã xong.
        _declaredSession = session;
    }

    // Khai báo lại toàn bộ node: NBIRTH, rồi một DBIRTH cho mỗi channel, y hệt như lúc connect.
    //
    // Được republish, không được đo lại. Các reading mang theo đồng hồ device tại thời điểm channel
    // được khai báo, nên chúng là cùng natural key mà lần khai báo đầu tiên đã mang theo: database chỉ
    // lưu chúng một lần và nhà máy chỉ đo chúng một lần. FormationChannel.Declare là nơi quyết định
    // điều đó, và nó được quyết định bởi khoảnh khắc chứ không phải ở đây - một rebirth bị đếm lần thứ
    // hai sẽ khiến vế trái của D1 vượt vế phải đúng bằng một DBIRTH trọn vẹn cho mỗi channel mỗi lần
    // rebirth, đo được chính xác là -48 trên một line tám channel.
    private async Task RepublishBirthsAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        if (now - _lastRebirth < RebirthCooldown)
        {
            return;
        }

        _lastRebirth = now;
        Interlocked.Increment(ref _rebirths);

        RebirthAnswered(_logger, _line.Path.Value, _line.ChannelCount);

        // Connect() nằm trong lock, không chỉ trong publishing. Nó reset sequence counter và cycle
        // state của từng channel, nên soạn nó trong khi Advance đang chạy dở sẽ đan xen hai luồng seq
        // và một consumer đang đếm chúng sẽ thấy những gap chưa từng xảy ra.
        await _publishing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await PublishLockedAsync(_line.Connect(ProcessElapsed)).ConfigureAwait(false);
        }
        finally
        {
            _publishing.Release();
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rebirth requested: line {Line} re-declaring itself and its {Channels} channels")]
    private static partial void RebirthAnswered(ILogger logger, string line, int channels);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Line {Line} could not reach the broker; the plant keeps running and the next session carries on")]
    private static partial void PublishFailed(ILogger logger, Exception error, string line);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Line {Line} abandoned the rest of a batch numbered under session {Session}, which has ended")]
    private static partial void SessionEndedMidBatch(ILogger logger, string line, ulong session);

    // SemaphoreSlim không tái nhập (không reentrant), nên các entry point tự acquire còn hàm này thì
    // mặc định đã có sẵn lock.
    //
    // Không nhận cancellation token, có chủ đích. Mọi thứ trong mảng này đã được đo rồi, nên không có
    // điểm nào sau bước soạn mà việc dừng lại là miễn phí cả - run dừng lại giữa hai tick thay vì ở
    // đây. Một publish đã bắt đầu thì cũng được phép hoàn tất, vì KẾT QUẢ của nó chính là điều report
    // phải mô tả: hủy giữa chừng một await sẽ để lại một message mà broker rất có thể đã lưu trong khi
    // process này không thể nói chắc theo cách nào, và cả D1 lẫn D3 đều không có cách nào biểu diễn
    // "có lẽ đã được gửi".
    private async Task PublishLockedAsync(ImmutableArray<ComposedMessage> messages)
    {
        var session = _line.BirthDeathSequence;

        // Đã soạn, do đó được đếm - không phải "được broker chấp nhận". Fault injector trả lời một
        // publish trong lúc dropout mô phỏng bằng cách giữ message trong bộ nhớ rồi trả về thành công,
        // nên nếu một counter được tăng khi trả về thành công thì nó sẽ khẳng định đã giao hàng cho
        // một thứ vẫn còn đang nằm trên device.
        LogicalMessageCount += messages.Length;

        for (var index = 0; index < messages.Length; index++)
        {
            // Một batch được soạn dưới một session và chỉ hợp lệ dưới đúng session đó. Đường truyền có
            // thể chết rồi quay lại trong khi vòng lặp này đang ở giữa hai message - nó không giữ một
            // thread nào cả - và phần còn lại đã được đánh số bởi session đã kết thúc: publish nó bây
            // giờ sẽ đặt một seq cũ nằm trước cả NBIRTH của session mới, chính xác là cái gap khiến một
            // consumer yêu cầu rebirth. Một message thuộc về một session không còn ai đếm nữa thì
            // không thể publish được.
            if (_line.BirthDeathSequence != session)
            {
                SessionEndedMidBatch(_logger, _line.Path.Value, session);
                Abandon(messages, index);

                return;
            }

            try
            {
                await _publisher.PublishAsync(messages[index].Message, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (SparkplugPublishException)
            {
                // Message ném exception bị bỏ dở cùng với phần còn lại của batch. Broker có thể đã lưu
                // hoặc chưa lưu nó, và một report đoán mò theo hướng nào cũng sẽ bịa ra con số mà D1
                // đem so với các dòng trong bảng - nên nó được đặt tên là đã mất, điều này khiến gate
                // chuyển đỏ và cho biết nên nhìn vào phía nào của đường truyền.
                Abandon(messages, index);

                throw;
            }
        }
    }

    // Không bao giờ bị trừ khỏi bất cứ thứ gì. Các reading vẫn nằm ở chỗ các channel đặt chúng, nên
    // reconciliation vẫn fail vì chúng; số này chỉ ghi lại bao nhiêu phần của thất bại đó xảy ra trước
    // broker thay vì sau broker.
    private void Abandon(ImmutableArray<ComposedMessage> messages, int from)
    {
        for (var index = from; index < messages.Length; index++)
        {
            AbandonedMeasurements += messages[index].Measurements;
        }
    }
}
