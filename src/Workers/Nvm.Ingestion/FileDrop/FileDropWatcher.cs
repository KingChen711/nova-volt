namespace Nvm.Ingestion.FileDrop;

/// <summary>Poll inbox và giao mỗi file đã publish cho processor.</summary>
public sealed partial class FileDropWatcher : BackgroundService
{
    private readonly FileDropProcessor _processor;
    private readonly FileDropOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<FileDropWatcher> _logger;

    /// <summary>Các file đã báo cáo là đang chờ, để một export bị kẹt chỉ là một dòng log.</summary>
    private readonly HashSet<string> _reportedUnpublished = new(StringComparer.Ordinal);

    /// <summary>Tạo watcher.</summary>
    /// <param name="processor">File được biến thành gì.</param>
    /// <param name="options">Thư mục và timing.</param>
    /// <param name="clock">Điều khiển poll interval và settle check (K1).</param>
    /// <param name="logger">Structured log sink.</param>
    public FileDropWatcher(
        FileDropProcessor processor,
        FileDropOptions options,
        TimeProvider clock,
        ILogger<FileDropWatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _processor = processor;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor.EnsureDirectories();
        WatchingInbox(_logger, _options.InboxPath, _options.PollInterval);

        if (_options.RequiresPublishedSuffix)
        {
            PublishContractInForce(_logger, _options.PublishedSuffix);
        }
        else
        {
            // Nói ra thành lời, một lần, đúng vào khoảnh khắc duy nhất có ai đó đang đọc. Một
            // deployment đã tắt contract này là đang đọc file theo một timer và coi một exporter im
            // lặng là một exporter đã xong, và quyết định đó không được phép chỉ khám phá ra bằng
            // cách đọc configuration.
            PublishContractDisabled(_logger, _options.SettleTime);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var present = new HashSet<string>(StringComparer.Ordinal);

                foreach (var path in EnumerateInbox())
                {
                    present.Add(path);

                    if (!IsReadable(path))
                    {
                        ReportIfWaitingTooLong(path);
                        continue;
                    }

                    _reportedUnpublished.Remove(path);
                    await _processor.ProcessAsync(path, stoppingToken);
                }

                // Một cái tên đã rời khỏi inbox là một cái tên đáng báo cáo lại nếu nó quay trở về.
                _reportedUnpublished.IntersectWith(present);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Một share biến mất, một file bị exporter khóa, một thay đổi permission. Không cái
                // nào trong số đó là lý do để dừng watching: đường MQTT không bị ảnh hưởng và file
                // vẫn còn trên đĩa để thử lại (N15).
                PollFailed(_logger, exception, _options.InboxPath);
            }

            await Task.Delay(_options.PollInterval, _clock, stoppingToken);
        }
    }

    /// <summary>Mọi thứ trong inbox khi một contract đang có hiệu lực, để phần còn lại có thể báo cáo.</summary>
    /// <remarks>
    /// Một file không ai từng đọc là thất bại mà contract này đánh đổi để tránh, và tự nó không có
    /// lỗi nào cả. Liệt kê toàn bộ thư mục là thứ giúp nói ra được điều đó.
    /// </remarks>
    private IEnumerable<string> EnumerateInbox() =>
        Directory
            .EnumerateFiles(_options.InboxPath, _options.RequiresPublishedSuffix ? "*" : "*.csv")
            .Order(StringComparer.Ordinal);

    /// <summary>Producer đã nói file này xong chưa.</summary>
    /// <remarks>
    /// Cái tên trả lời câu hỏi này; settle time chỉ đoán mò. Đổi tên một file ra khỏi inbox không
    /// đóng cái handle mà một exporter vẫn còn giữ trên nó, nên một mtime im lặng chỉ là bằng chứng
    /// cho việc exporter đã im lặng được hai giây, không hơn.
    /// </remarks>
    private bool IsReadable(string path)
    {
        if (!_options.RequiresPublishedSuffix)
        {
            return HasSettled(path);
        }

        if (!path.EndsWith(_options.PublishedSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        // Vẫn là một export bên dưới, kiểm tra bằng tay thay vì bằng glob: kiểu khớp DOS-style trên
        // Windows có thể trả về một cái tên mà extension chỉ trông giống suffix, và bất cứ thứ gì
        // khác trong inbox là việc của ai đó khác, không phải của adapter này.
        return path[..^_options.PublishedSuffix.Length]
            .EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
    }

    private void ReportIfWaitingTooLong(string path)
    {
        if (!_options.RequiresPublishedSuffix || !File.Exists(path))
        {
            // Không có gì đang chờ: hoặc không có contract nào để chờ theo, hoặc file đã bị claim
            // hoặc bị xóa giữa lúc liệt kê và lúc này.
            return;
        }

        var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        var waited = _clock.GetUtcNow() - lastWrite;

        if (waited >= _options.UnpublishedWarningAfter && _reportedUnpublished.Add(path))
        {
            UnpublishedFileWaiting(
                _logger,
                Path.GetFileName(path),
                waited,
                Path.GetFileNameWithoutExtension(path) + ".csv" + _options.PublishedSuffix);
        }
    }

    private bool HasSettled(string path)
    {
        if (_options.SettleTime <= TimeSpan.Zero)
        {
            return true;
        }

        // Cả hai vế đều UTC, và vế gần đi qua TimeProvider (K1). Vế xa là một timestamp do hệ điều
        // hành ghi, nên một test lái theo một đồng hồ giả có một giá trị thật trong phép so sánh —
        // đó là lý do một test không nói về settling thì đặt SettleTime bằng 0 thay vì cố dịch mtime
        // của file.
        var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

        return _clock.GetUtcNow() - lastWrite >= _options.SettleTime;
    }

    [LoggerMessage(
        EventId = 2504,
        Level = LogLevel.Information,
        Message = "Watching {InboxPath} for dropped CSV files every {PollInterval}")]
    private static partial void WatchingInbox(ILogger logger, string inboxPath, TimeSpan pollInterval);

    [LoggerMessage(
        EventId = 2505,
        Level = LogLevel.Error,
        Message = "Could not read the file-drop inbox {InboxPath}; the next poll will try again")]
    private static partial void PollFailed(ILogger logger, Exception exception, string inboxPath);

    [LoggerMessage(
        EventId = 2512,
        Level = LogLevel.Information,
        Message =
            "File drop reads an export only once it is named '<name>.csv{PublishedSuffix}'; producers "
            + "write to a temporary name, close it, and rename it into place in one step")]
    private static partial void PublishContractInForce(ILogger logger, string publishedSuffix);

    [LoggerMessage(
        EventId = 2513,
        Level = LogLevel.Warning,
        Message =
            "File drop has no publish contract: an export is read after {SettleTime} of quiet, which "
            + "cannot tell a finished file from an exporter that paused, so a partial run can be "
            + "stored and its tail lost")]
    private static partial void PublishContractDisabled(ILogger logger, TimeSpan settleTime);

    [LoggerMessage(
        EventId = 2514,
        Level = LogLevel.Warning,
        Message =
            "File drop '{FileName}' has been in the inbox {Waited} and has not been read because it "
            + "is not published; the exporter either has not finished it or never renames its "
            + "exports to '{PublishedName}'")]
    private static partial void UnpublishedFileWaiting(
        ILogger logger,
        string fileName,
        TimeSpan waited,
        string publishedName);
}
