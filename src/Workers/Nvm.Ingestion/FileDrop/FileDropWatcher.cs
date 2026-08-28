namespace Nvm.Ingestion.FileDrop;

/// <summary>Polls the inbox and hands each settled file to the processor.</summary>
public sealed partial class FileDropWatcher : BackgroundService
{
    private const string Pattern = "*.csv";

    private readonly FileDropProcessor _processor;
    private readonly FileDropOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<FileDropWatcher> _logger;

    /// <summary>Creates the watcher.</summary>
    /// <param name="processor">What a file is turned into.</param>
    /// <param name="options">Directories and timing.</param>
    /// <param name="clock">Drives the poll interval and the settle check (K1).</param>
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(_options.InboxPath, Pattern).Order(StringComparer.Ordinal))
                {
                    if (!HasSettled(path))
                    {
                        continue;
                    }

                    await _processor.ProcessAsync(path, stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A share that went away, a file locked by the exporter, a permission change. None of
                // those is a reason to stop watching: the MQTT path is unaffected and the file is
                // still on disk to be tried again (N15).
                PollFailed(_logger, exception, _options.InboxPath);
            }

            await Task.Delay(_options.PollInterval, _clock, stoppingToken);
        }
    }

    private bool HasSettled(string path)
    {
        if (_options.SettleTime <= TimeSpan.Zero)
        {
            return true;
        }

        // Both sides UTC, and the near side through TimeProvider (K1). The far side is a timestamp
        // the operating system wrote, so a test driving a fake clock has one real value in the
        // comparison — which is why a test that is not about settling sets SettleTime to zero rather
        // than trying to move the file's mtime.
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
}
