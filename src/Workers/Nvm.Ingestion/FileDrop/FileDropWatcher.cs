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
    /// <param name="clock">Điều khiển poll interval và cảnh báo file chờ publish (K1).</param>
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

        PublishContractInForce(_logger, _options.PublishedSuffix);

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
            .EnumerateFiles(_options.InboxPath)
            .Order(StringComparer.Ordinal);

    /// <summary>Producer đã nói file này xong chưa.</summary>
    /// <remarks>
    /// Cái tên trả lời câu hỏi này; settle time chỉ đoán mò. Đổi tên một file ra khỏi inbox không
    /// đóng cái handle mà một exporter vẫn còn giữ trên nó, nên một mtime im lặng chỉ là bằng chứng
    /// cho việc exporter đã im lặng được hai giây, không hơn.
    /// </remarks>
    private bool IsReadable(string path)
    {
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
        if (!File.Exists(path))
        {
            // Producer có thể rename file sau lúc liệt kê; không báo chờ cho tên đã biến mất.
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
