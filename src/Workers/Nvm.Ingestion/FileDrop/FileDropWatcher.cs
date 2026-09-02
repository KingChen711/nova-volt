namespace Nvm.Ingestion.FileDrop;

/// <summary>Polls the inbox and hands each published file to the processor.</summary>
public sealed partial class FileDropWatcher : BackgroundService
{
    private readonly FileDropProcessor _processor;
    private readonly FileDropOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<FileDropWatcher> _logger;

    /// <summary>Files already reported as waiting, so one stuck export is one log line.</summary>
    private readonly HashSet<string> _reportedUnpublished = new(StringComparer.Ordinal);

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

        if (_options.RequiresPublishedSuffix)
        {
            PublishContractInForce(_logger, _options.PublishedSuffix);
        }
        else
        {
            // Said out loud, once, at the only moment somebody is reading. A deployment that turned
            // the contract off is reading files on a timer and calling a quiet exporter a finished
            // one, and that decision must not be discoverable only by reading the configuration.
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

                // A name that left the inbox is a name worth reporting again if it comes back.
                _reportedUnpublished.IntersectWith(present);
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

    /// <summary>Everything in the inbox when a contract is in force, so the rest can be reported.</summary>
    /// <remarks>
    /// A file nobody will ever read is the failure this contract trades for, and it has no error of
    /// its own. Listing the whole directory is what makes it possible to say so.
    /// </remarks>
    private IEnumerable<string> EnumerateInbox() =>
        Directory
            .EnumerateFiles(_options.InboxPath, _options.RequiresPublishedSuffix ? "*" : "*.csv")
            .Order(StringComparer.Ordinal);

    /// <summary>Whether the producer has said this file is finished.</summary>
    /// <remarks>
    /// The name answers the question; the settle time only guesses at it. Renaming a file out of the
    /// inbox does not close the handle an exporter still holds on it, so a quiet mtime is evidence
    /// of nothing except that the exporter has been quiet for two seconds.
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

        // Still an export underneath, checked by hand rather than by the glob: DOS-style matching on
        // Windows can hand back a name whose extension only looks like the suffix, and anything else
        // in the inbox is somebody's business but not this adapter's.
        return path[..^_options.PublishedSuffix.Length]
            .EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
    }

    private void ReportIfWaitingTooLong(string path)
    {
        if (!_options.RequiresPublishedSuffix || !File.Exists(path))
        {
            // Nothing is waiting: either there is no contract to wait on, or the file was claimed or
            // removed between the listing and here.
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
