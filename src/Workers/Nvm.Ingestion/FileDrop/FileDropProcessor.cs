using System.Globalization;
using Nvm.Ingestion.Persistence;

namespace Nvm.Ingestion.FileDrop;

/// <summary>Turns one dropped file into stored measurements, and files the remains.</summary>
/// <remarks>
/// Separated from the polling loop so the whole decision — parse, ingest, move, explain — can be
/// tested against a real directory without a host, a timer, or a wait.
/// </remarks>
public sealed partial class FileDropProcessor
{
    private readonly CsvMeasurementReader _reader;
    private readonly IMeasurementIngestor _ingestor;
    private readonly FileDropOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<FileDropProcessor> _logger;

    /// <summary>Creates the processor over the one dedup path.</summary>
    /// <param name="reader">CSV parser.</param>
    /// <param name="ingestor">The same ingestor the MQTT path uses (C15.1).</param>
    /// <param name="options">Directories and timing.</param>
    /// <param name="clock">Stamps <c>gateway_timestamp</c> as the moment the file was read (K1).</param>
    /// <param name="logger">Structured log sink.</param>
    public FileDropProcessor(
        CsvMeasurementReader reader,
        IMeasurementIngestor ingestor,
        FileDropOptions options,
        TimeProvider clock,
        ILogger<FileDropProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(ingestor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _ingestor = ingestor;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Creates the inbox and its two outcome directories.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(_options.InboxPath);
        Directory.CreateDirectory(_options.ProcessedPath);
        Directory.CreateDirectory(_options.RejectedPath);
    }

    /// <summary>Processes one file and moves it out of the inbox.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Stops the work when the host shuts down.</param>
    /// <returns>What was stored, or null when the file could not be read at all.</returns>
    public async Task<IngestionResult?> ProcessAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var name = Path.GetFileName(path);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);

        FileDropParseResult parsed;

        try
        {
            parsed = _reader.Read(lines);
        }
        catch (FileDropFormatException exception)
        {
            // The whole file, not a line. Nothing in it can be attributed, so nothing in it is stored.
            Move(path, Path.Combine(_options.RejectedPath, name));
            await WriteErrorAsync(
                Path.Combine(_options.RejectedPath, name + ".error"),
                [$"The file could not be read: {exception.Message}"],
                cancellationToken);

            FileRejected(_logger, name, exception.Message);
            return null;
        }

        var result = parsed.Measurements.Count == 0
            ? new IngestionResult(0, 0)
            : await _ingestor.IngestAsync(parsed.Measurements, _clock.GetUtcNow(), cancellationToken);

        if (parsed.Rejected.Count > 0)
        {
            await RejectLinesAsync(name, parsed.Rejected, cancellationToken);
        }

        // Moved only after the transaction committed. The other order would leave a window where the
        // file is marked processed and nothing was stored, and a crash inside it loses the export
        // silently. Moving late can only ever repeat a file, and a repeat is what dedup is for.
        Move(path, Path.Combine(_options.ProcessedPath, name));

        FileProcessed(_logger, name, result.Inserted, result.Duplicates, parsed.Rejected.Count);
        return result;
    }

    private async Task RejectLinesAsync(
        string name,
        IReadOnlyList<RejectedLine> rejected,
        CancellationToken cancellationToken)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        foreach (var line in rejected)
        {
            var suffix = string.Format(CultureInfo.InvariantCulture, "{0}.line-{1}{2}", stem, line.LineNumber, extension);
            var target = Path.Combine(_options.RejectedPath, suffix);

            // The header goes with it. A rejected line on its own is a row of commas an operator has
            // to decode by counting; with the header above it, it reads.
            await File.WriteAllLinesAsync(
                target,
                [CsvMeasurementReader.Header, line.Line],
                cancellationToken);

            await WriteErrorAsync(
                target + ".error",
                [
                    $"Line {line.LineNumber} of '{name}' was not stored.",
                    line.Reason,
                    string.Empty,
                    "The other lines of the file were stored. Fix this one and drop it in again;",
                    "a line that was already stored is recognised and will not be stored twice.",
                ],
                cancellationToken);

            LineRejected(_logger, name, line.LineNumber, line.Reason);
        }
    }

    private static async Task WriteErrorAsync(
        string path,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken) =>
        await File.WriteAllLinesAsync(path, lines, cancellationToken);

    private static void Move(string source, string target)
    {
        // Overwrite. A tester that re-exports the same file name is normal, and refusing would leave
        // the second one in the inbox forever while the log filled with the same failure.
        File.Move(source, target, overwrite: true);
    }

    [LoggerMessage(
        EventId = 2501,
        Level = LogLevel.Information,
        Message = "File drop '{FileName}': stored={Inserted}, duplicates={Duplicates}, rejected_lines={RejectedLines}")]
    private static partial void FileProcessed(
        ILogger logger,
        string fileName,
        int inserted,
        int duplicates,
        int rejectedLines);

    [LoggerMessage(
        EventId = 2502,
        Level = LogLevel.Error,
        Message = "File drop '{FileName}' rejected whole: {Reason}")]
    private static partial void FileRejected(ILogger logger, string fileName, string reason);

    [LoggerMessage(
        EventId = 2503,
        Level = LogLevel.Warning,
        Message = "File drop '{FileName}' line {LineNumber} rejected: {Reason}")]
    private static partial void LineRejected(ILogger logger, string fileName, int lineNumber, string reason);
}
