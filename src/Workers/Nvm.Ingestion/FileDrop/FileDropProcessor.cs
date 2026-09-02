using System.Globalization;
using System.Text;
using Nvm.Ingestion.Persistence;
using Nvm.Ingestion.RawCurves;
using Nvm.Kernel.Identity;

namespace Nvm.Ingestion.FileDrop;

/// <summary>Turns one dropped file into stored measurements, and files the remains.</summary>
/// <remarks>
/// Separated from the polling loop so the whole decision — parse, ingest, move, explain — can be
/// tested against a real directory without a host, a timer, or a wait.
/// </remarks>
public sealed partial class FileDropProcessor
{
    /// <summary>Named in every archive row this adapter writes, instead of a service account.</summary>
    private const string ArchiveActor = "ingestion:file-drop";

    private readonly CsvMeasurementReader _reader;
    private readonly IMeasurementIngestor _ingestor;
    private readonly FileDropOptions _options;
    private readonly TimeProvider _clock;
    private readonly IRawCurveArchive? _archive;
    private readonly ILogger<FileDropProcessor> _logger;

    private string ProcessingPath => Path.Combine(_options.InboxPath, ".processing");

    /// <summary>Creates the processor over the one dedup path.</summary>
    /// <param name="reader">CSV parser.</param>
    /// <param name="ingestor">The same ingestor the MQTT path uses (C15.1).</param>
    /// <param name="options">Directories and timing.</param>
    /// <param name="clock">Stamps <c>gateway_timestamp</c> as the moment the file was read (K1).</param>
    /// <param name="logger">Structured log sink.</param>
    /// <param name="archive">
    /// Where the original bytes are kept, or null where no WORM store is configured. A file with
    /// measurements fails closed when this is null: a plant that stores measurements and throws the
    /// export away has no answer to "what did the machine actually write" (C12.1).
    /// </param>
    public FileDropProcessor(
        CsvMeasurementReader reader,
        IMeasurementIngestor ingestor,
        FileDropOptions options,
        TimeProvider clock,
        ILogger<FileDropProcessor> logger,
        IRawCurveArchive? archive = null)
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
        _archive = archive;
        _logger = logger;
    }

    /// <summary>Creates the inbox and its two outcome directories.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(_options.InboxPath);
        Directory.CreateDirectory(_options.ProcessedPath);
        Directory.CreateDirectory(_options.RejectedPath);
        Directory.CreateDirectory(ProcessingPath);

        RecoverAbandonedClaims();
    }

    /// <summary>Processes one file and moves it out of the inbox.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Stops the work when the host shuts down.</param>
    /// <returns>What was stored, or null when the whole file was rejected.</returns>
    public async Task<IngestionResult?> ProcessAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var name = ExportName(Path.GetFileName(path));
        var claim = Claim(path, name);

        try
        {
            // Read once after the atomic claim. Parsing, WORM archival and the outcome copy all use
            // this one immutable buffer. Reopening either the inbox path or the claimed file would
            // let an exporter append/replace bytes between those phases and produce telemetry from A
            // with an alleged original B.
            var snapshot = await File.ReadAllBytesAsync(claim.SnapshotPath, cancellationToken);
            return await ProcessSnapshotAsync(claim, snapshot, name, cancellationToken);
        }
        catch
        {
            // Cancellation does not cancel recovery. Losing the claimed original while the host is
            // stopping is worse than taking a little longer to stop.
            if (File.Exists(claim.SnapshotPath))
            {
                try
                {
                    // The claimed file is the snapshot: nothing writes into `.processing`, so the
                    // bytes read a moment ago and the bytes on disk are the same bytes. Moving that
                    // file back is one rename, where writing the buffer out again was a create and
                    // a rename with a gap in the middle for a crash to stop in.
                    RepublishClaim(claim, "retry");
                }
                catch (Exception restoreFailure)
                {
                    // A failed restore must not replace the failure that caused it. Throwing from
                    // here reported the inbox name as the problem and threw away the real cause --
                    // the archive was unreachable, the transaction rolled back -- while leaving the
                    // claim under `.processing` until the next restart. Both are worth a log line;
                    // only one is worth an exception, and it is the one already on its way out.
                    RestoreFailed(_logger, restoreFailure, name);
                }
            }

            throw;
        }
        finally
        {
            DeleteEmptyClaimDirectory(claim.DirectoryPath);
        }
    }

    private async Task<IngestionResult?> ProcessSnapshotAsync(
        ClaimedFile claim,
        byte[] snapshot,
        string name,
        CancellationToken cancellationToken)
    {
        var lines = DecodeLines(snapshot);

        FileDropParseResult parsed;

        try
        {
            parsed = _reader.Read(lines);
        }
        catch (FileDropFormatException exception)
        {
            // The whole file, not a line. Nothing in it can be attributed, so nothing in it is stored.
            await WriteErrorAsync(
                Path.Combine(_options.RejectedPath, name + ".error"),
                [$"The file could not be read: {exception.Message}"],
                cancellationToken);
            await CompleteSnapshotAsync(
                claim,
                snapshot,
                Path.Combine(_options.RejectedPath, name),
                cancellationToken);

            FileRejected(_logger, name, exception.Message);
            return null;
        }

        // Every data line, not only the ones that parsed all the way through. A line can fail on its
        // VALUE and still name its machine perfectly clearly, and counting only successful parses let
        // exactly that file through: machine A's rows stored, the whole A+B file archived under A,
        // and the file filed as processed. An independent audit found this on 2026-09-01, after the
        // previous round had already "closed" the mixed-machine hole once.
        var machines = parsed.Measurements
            .Select(measurement => measurement.EquipmentPath)
            .Concat(parsed.Rejected.Where(line => line.Identity is not null).Select(line => line.Identity!))
            .Distinct()
            .ToList();

        // A line whose identity could not be read at all is worse than a second machine: it might BE
        // a second machine and there is no way to tell. The file then cannot be proven to belong to
        // anyone, and an archive filed under a machine that might not own every line in it is not
        // evidence. This is a deliberate departure from "one bad line does not reject the file" in
        // CsvMeasurementReader: a bad VALUE costs one measurement, a bad IDENTITY costs the ability
        // to say whose file this is.
        var unattributable = parsed.Rejected.Count(line => line.Identity is null);
        var hasDataLines = parsed.Measurements.Count > 0 || parsed.Rejected.Count > 0;

        // A syntactically valid CSV with no readings is not a successful no-op. It cannot be
        // attributed to a machine, so it cannot have the immutable original that `processed`
        // promises. More importantly, treating it as success hides the ordinary plant failure where
        // an exporter emitted a header after losing the run it was supposed to export.
        if (!hasDataLines)
        {
            await WriteErrorAsync(
                Path.Combine(_options.RejectedPath, name + ".error"),
                [
                    $"'{name}' has a valid CSV header but no data lines, so nothing was stored.",
                    string.Empty,
                    "An empty export can mean the equipment exporter lost or skipped the run. It is",
                    "rejected rather than marked processed so that absence remains visible to an",
                    "operator. Re-export the run and drop the resulting file in again.",
                ],
                cancellationToken);
            await CompleteSnapshotAsync(
                claim,
                snapshot,
                Path.Combine(_options.RejectedPath, name),
                cancellationToken);

            FileRejectedEmptyExport(_logger, name);
            return null;
        }

        // Refused before anything is stored, and refused whether or not an archive is configured. An
        // export is one machine's record of one run; a file naming several has no single answer to
        // "whose curve is this", so it cannot be archived as anybody's original — and a file that can
        // never have an original must not be able to succeed. The earlier version of this stored the
        // rows, logged a warning and filed the file under `processed`, which is the failure this
        // whole adapter exists to avoid: something that looks handled and is not.
        if (machines.Count != 1 || unattributable > 0)
        {
            await WriteErrorAsync(
                Path.Combine(_options.RejectedPath, name + ".error"),
                [
                    unattributable > 0
                        ? $"'{name}' has {unattributable} line(s) whose equipment path could not be "
                            + "read, so the file cannot be proven to belong to one machine. Nothing "
                            + "in it was stored."
                        : $"'{name}' names {machines.Count} machines, so nothing in it was stored.",
                    string.Empty,
                    "One export is one machine's record of one run. A file covering several has no",
                    "single original to keep, and a measurement whose original cannot be produced is",
                    "not evidence (AGENTS.md K4).",
                    string.Empty,
                    "Machines named by this file, including on lines that failed for other reasons:",
                    .. machines.Select(machine => "  " + machine.Value),
                    string.Empty,
                    "Split the export so each file names one equipment path, then drop the parts in",
                    "again. Nothing was stored, so nothing will be stored twice.",
                ],
                cancellationToken);
            await CompleteSnapshotAsync(
                claim,
                snapshot,
                Path.Combine(_options.RejectedPath, name),
                cancellationToken);

            if (unattributable > 0)
            {
                FileRejectedUnreadableIdentity(_logger, name, unattributable);
            }
            else
            {
                FileRejectedMixedEquipment(_logger, name);
            }

            return null;
        }

        // A file that HAD data lines and produced no measurement does not belong in `processed`.
        //
        // Nothing was stored, so nothing lacks its original and K4 is not violated — but the export
        // still leaves the inbox and lands in the directory whose name says its contents went in.
        // An operator reading `processed` cannot tell this file apart from one that worked, and the
        // rejected-lines artefact that explains it sits in a different directory. The signal and the
        // outcome pointed in opposite directions.
        //
        if (parsed.Measurements.Count == 0)
        {
            await RejectLinesAsync(name, parsed.Rejected, cancellationToken);
            await WriteErrorAsync(
                Path.Combine(_options.RejectedPath, name + ".error"),
                [
                    $"'{name}' has {parsed.Rejected.Count} data line(s) and not one of them could be "
                        + "read, so nothing was stored.",
                    string.Empty,
                    "The file is filed under rejected rather than processed because `processed`",
                    "means an export whose readings went in, and none of these did. Line-by-line",
                    "reasons are in the accompanying rejection file.",
                    string.Empty,
                    "Nothing was stored, so nothing will be stored twice when the corrected export",
                    "is dropped in again.",
                ],
                cancellationToken);
            await CompleteSnapshotAsync(
                claim,
                snapshot,
                Path.Combine(_options.RejectedPath, name),
                cancellationToken);

            FileRejectedAllLinesUnreadable(_logger, name, parsed.Rejected.Count);
            return null;
        }

        var descriptor = Describe(parsed.Measurements)
            ?? throw new InvalidOperationException(
                $"'{name}' passed whole-file validation without one attributable raw-curve descriptor.");

        var result = await _ingestor.IngestAsync(
            parsed.Measurements,
            _clock.GetUtcNow(),
            cancellationToken);

        if (parsed.Rejected.Count > 0)
        {
            await RejectLinesAsync(name, parsed.Rejected, cancellationToken);
        }

        // Before the outcome copy, and after the rows are stored. If the archive is unreachable the
        // exact claimed snapshot is restored to the inbox and the next poll tries again: the rows are
        // already in, so the retry costs a round of deduplication and buys the guarantee that no file
        // reaches `processed` without its original bytes kept. Program startup and the guard below
        // both fail closed when file drop has no archive.
        await ArchiveOriginalAsync(snapshot, name, descriptor, cancellationToken);

        // Published only after the transaction committed. The other order would leave a window where
        // the file is marked processed and nothing was stored, and a crash inside it loses the export
        // silently. Publishing late can only repeat a snapshot, and a repeat is what dedup is for.
        await CompleteSnapshotAsync(
            claim,
            snapshot,
            Path.Combine(_options.ProcessedPath, name),
            cancellationToken);

        FileProcessed(_logger, name, result.Inserted, result.Duplicates, parsed.Rejected.Count);
        return result;
    }

    /// <summary>Keeps the exact bytes of one export beside the rows read out of it.</summary>
    /// <remarks>
    /// The interval comes from the measurements rather than from the file name, and its end is
    /// exclusive: the archive interval is half-open like every other interval in this system, so the
    /// last sample is inside it and the next export's first sample is not. A file with no descriptor
    /// never reaches here — <see cref="ProcessAsync"/> refuses it before anything is stored.
    /// </remarks>
    private async Task ArchiveOriginalAsync(
        byte[] snapshot,
        string name,
        RawCurveDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (_archive is null)
        {
            // NOT a warning-and-carry-on. Moving the file to `processed` here consumed the export and
            // destroyed the only copy of bytes an auditor is entitled to ask for, which is the exact
            // outcome C12.1 exists to prevent. The comment that used to sit above this branch
            // described the hole instead of closing it.
            //
            // Program.cs refuses to start when the file drop is enabled without an archive, so a
            // correctly deployed service never reaches this line. It stays as the second lock,
            // because the first one only guards the composition root and this class is also
            // constructed directly by tests and by anything written later.
            throw new RawCurveArchiveMissingException(
                $"'{name}' names {descriptor.EquipmentPath.Value} and would be stored with no archive "
                + "configured, so its original bytes would be lost. Configure "
                + "NVM_INGEST__RawCurveArchive, or stop the file drop.");
        }

        await using var original = new MemoryStream(snapshot, writable: false);
        var result = await _archive.ArchiveAsync(
            descriptor,
            original,
            new RawCurveProvenance(
                ArchiveActor,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Original export '{name}' read from the file drop")),
            cancellationToken);

        FileArchived(_logger, name, result.Sha256, result.ObjectCreated);
    }

    /// <summary>The one machine and interval a file covers, or null when it covers several.</summary>
    private static RawCurveDescriptor? Describe(IReadOnlyList<FileMeasurement> measurements)
    {
        if (measurements.Count == 0)
        {
            return null;
        }

        EquipmentPath? equipmentPath = null;
        string? unitId = null;
        var oneUnit = true;
        var first = DateTimeOffset.MaxValue;
        var last = DateTimeOffset.MinValue;

        foreach (var measurement in measurements)
        {
            if (equipmentPath is null)
            {
                equipmentPath = measurement.EquipmentPath;
                unitId = measurement.UnitId;
            }
            else if (!string.Equals(
                equipmentPath.Value,
                measurement.EquipmentPath.Value,
                StringComparison.Ordinal))
            {
                return null;
            }
            else if (!string.Equals(unitId, measurement.UnitId, StringComparison.Ordinal))
            {
                // Several cells through one channel in one export is ordinary. The file is still one
                // machine's original, so it is archived — with no unit named, rather than with the
                // first one, which would file the whole export under one cell's serial.
                oneUnit = false;
            }

            var at = measurement.Reading.DeviceTimestamp;
            first = at < first ? at : first;
            last = at > last ? at : last;
        }

        return new RawCurveDescriptor(
            equipmentPath!,
            oneUnit ? unitId : null,
            first,
            // One microsecond, not one tick: the interval is stored in a `timestamptz`, and an end
            // that only exists at 100 ns resolution is an end the archive cannot keep. A file with a
            // single reading is the case that proves it -- ended one tick after it began, it stored
            // as an interval of zero and PostgreSQL refused the row.
            last.AddTicks(TimeSpan.TicksPerMicrosecond));
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

    private static List<string> DecodeLines(byte[] snapshot)
    {
        using var source = new MemoryStream(snapshot, writable: false);
        using var reader = new StreamReader(
            source,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        var lines = new List<string>();

        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private ClaimedFile Claim(string publishedPath, string exportName)
    {
        Directory.CreateDirectory(ProcessingPath);
        var directoryPath = Path.Combine(ProcessingPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var snapshotPath = Path.Combine(directoryPath, exportName);

        try
        {
            // One rename takes the whole export, because the export is one file. The version of this
            // that published readiness in a second file beside the data had to take the two in
            // sequence, and a producer republishing that name between the two renames handed the
            // reader one export's bytes under another export's readiness -- measured, not argued
            // (ADR-035 §Evidence, probe `PAIR_RACE`). No ordering of two renames closes that, so
            // there is one rename: the suffix the producer renamed the file into is the readiness,
            // and taking the file takes the readiness with it.
            File.Move(publishedPath, snapshotPath);
            return new ClaimedFile(snapshotPath, directoryPath);
        }
        catch
        {
            DeleteEmptyClaimDirectory(directoryPath);
            throw;
        }
    }

    /// <summary>The export's own name, refusing anything a producer has not published.</summary>
    /// <remarks>
    /// Checked here rather than only in the watcher because this is the claim boundary: a caller
    /// that hands over a half-written file has to be told no by the thing that would otherwise
    /// store it.
    /// </remarks>
    private string ExportName(string publishedName)
    {
        if (!_options.RequiresPublishedSuffix)
        {
            return publishedName;
        }

        if (!publishedName.EndsWith(_options.PublishedSuffix, StringComparison.Ordinal)
            || publishedName.Length == _options.PublishedSuffix.Length)
        {
            throw new FileDropNotPublishedException(
                $"'{publishedName}' has not been published, so it must not be read. A producer "
                + "writes the export under a temporary name, closes it, and renames it to "
                + $"'<name>.csv{_options.PublishedSuffix}'. That rename is the publish, and until it "
                + "lands the file belongs to the exporter.");
        }

        return publishedName[..^_options.PublishedSuffix.Length];
    }

    private string PublishedPath(string dataFilePath) =>
        _options.RequiresPublishedSuffix ? dataFilePath + _options.PublishedSuffix : dataFilePath;

    /// <summary>Publishes a claimed export back into the inbox under a name of its own.</summary>
    /// <remarks>
    /// Never the name it arrived under. A POSIX rename replaces whatever sits at the destination, so
    /// a producer publishing that name again cannot be defended against by any check made on this
    /// side -- an exclusive create is a placeholder, and `mv` walks straight through a placeholder
    /// (ADR-035 §Evidence, probe `RESERVATION_RACE`). The only way not to destroy somebody else's
    /// export is not to aim at a name somebody else might use. The GUID also tells an operator
    /// reading the inbox that this file has been round the loop once.
    /// </remarks>
    private void RepublishClaim(ClaimedFile claim, string reason)
    {
        var target = PublishedPath(
            Path.Combine(
                _options.InboxPath,
                UniqueName(Path.GetFileName(claim.SnapshotPath), reason)));

        // One rename again: afterwards the export is in the inbox and published, or it is still
        // claimed and recoverable. There is no third state for a crash to stop in.
        File.Move(claim.SnapshotPath, target);
    }

    private static async Task CompleteSnapshotAsync(
        ClaimedFile claim,
        byte[] snapshot,
        string target,
        CancellationToken cancellationToken)
    {
        await WriteSnapshotAtomicallyAsync(target, snapshot, cancellationToken);
        File.Delete(claim.SnapshotPath);
    }

    private static async Task WriteSnapshotAtomicallyAsync(
        string target,
        byte[] snapshot,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException($"'{target}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(temporary, snapshot, cancellationToken);

            // Overwrite, and only ever inside this adapter's own outcome directories, where
            // re-exporting a file name is ordinary and the immutable archive is content-addressed
            // anyway. Nothing writes into the inbox by this route: a file going back there goes by
            // rename, under a name no producer will pick.
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void RecoverAbandonedClaims()
    {
        foreach (var directory in Directory.EnumerateDirectories(ProcessingPath))
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                RepublishClaim(new ClaimedFile(path, directory), "recovered");
            }

            DeleteEmptyClaimDirectory(directory);
        }
    }

    /// <summary>Names a returned export after the one it came from, plus how many rounds it has had.</summary>
    /// <remarks>
    /// The marker replaces the previous marker rather than stacking on top of it. Appending to
    /// whatever name it was handed made every round of the retry loop ~40 bytes longer, and after
    /// six rounds the name passed the 255-byte limit: the move failed, and the export was stranded
    /// under `.processing` with nothing but a log line to say so. Found by
    /// `scripts/file-drop-race-probe.sh` on the running stack, where an export that could never be
    /// archived went round the loop until it could no longer be moved at all.
    /// </remarks>
    private static string UniqueName(string exportName, string reason)
    {
        var stem = Path.GetFileNameWithoutExtension(exportName);
        var extension = Path.GetExtension(exportName);
        var attempt = 1;

        if (TrySplitMarker(stem, out var origin, out var previousReason, out var previousAttempt))
        {
            stem = origin;
            attempt = string.Equals(previousReason, reason, StringComparison.Ordinal)
                ? previousAttempt + 1
                : 1;
        }

        var marker = attempt == 1
            ? reason
            : reason + attempt.ToString(CultureInfo.InvariantCulture);

        return $"{stem}.{marker}-{Guid.NewGuid():N}{extension}";
    }

    /// <summary>Reads a marker this adapter wrote, and nothing else.</summary>
    /// <remarks>
    /// Only <c>retry</c> and <c>recovered</c> are recognised, so a producer whose own file name ends
    /// in something-hex keeps every part of the name it chose.
    /// </remarks>
    private static bool TrySplitMarker(string stem, out string origin, out string reason, out int attempt)
    {
        const int GuidLength = 32;
        origin = stem;
        reason = string.Empty;
        attempt = 0;

        if (stem.Length < GuidLength + 3 || stem[^(GuidLength + 1)] != '-')
        {
            return false;
        }

        for (var index = stem.Length - GuidLength; index < stem.Length; index++)
        {
            if (!char.IsAsciiHexDigitLower(stem[index]))
            {
                return false;
            }
        }

        var markerStart = stem.LastIndexOf('.', stem.Length - (GuidLength + 2));

        if (markerStart < 0)
        {
            return false;
        }

        var marker = stem[(markerStart + 1)..^(GuidLength + 1)];
        var word = marker.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        if (word is not ("retry" or "recovered"))
        {
            return false;
        }

        origin = stem[..markerStart];
        reason = word;
        attempt = word.Length == marker.Length
            ? 1
            : int.Parse(marker[word.Length..], CultureInfo.InvariantCulture);

        return true;
    }

    private static void DeleteEmptyClaimDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath)
            && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
        {
            Directory.Delete(directoryPath);
        }
    }

    private sealed record ClaimedFile(string SnapshotPath, string DirectoryPath);

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
        EventId = 2506,
        Level = LogLevel.Information,
        Message = "File drop '{FileName}': original archived, sha256={Sha256}, new_object={ObjectCreated}")]
    private static partial void FileArchived(
        ILogger logger,
        string fileName,
        string sha256,
        bool objectCreated);

    [LoggerMessage(
        EventId = 2508,
        Level = LogLevel.Error,
        Message =
            "File drop '{FileName}' rejected whole: it names more than one machine, so it has no "
            + "single original to keep and nothing in it was stored")]
    private static partial void FileRejectedMixedEquipment(ILogger logger, string fileName);

    [LoggerMessage(
        EventId = 2511,
        Level = LogLevel.Error,
        Message =
            "File drop '{FileName}' rejected whole: {UnreadableIdentities} data line(s) have an "
            + "unreadable equipment identity, so the export cannot be attributed and nothing in it "
            + "was stored")]
    private static partial void FileRejectedUnreadableIdentity(
        ILogger logger,
        string fileName,
        int unreadableIdentities);

    [LoggerMessage(
        EventId = 2509,
        Level = LogLevel.Error,
        Message =
            "File drop '{FileName}' rejected whole: all {RejectedLines} data lines were unreadable, "
            + "so nothing was stored and the file does not belong in processed")]
    private static partial void FileRejectedAllLinesUnreadable(
        ILogger logger,
        string fileName,
        int rejectedLines);

    [LoggerMessage(
        EventId = 2510,
        Level = LogLevel.Error,
        Message =
            "File drop '{FileName}' rejected whole: the export has no data lines, so it cannot be "
            + "attributed or archived and may hide an exporter failure")]
    private static partial void FileRejectedEmptyExport(ILogger logger, string fileName);

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

    [LoggerMessage(
        EventId = 2507,
        Level = LogLevel.Error,
        Message =
            "File drop '{FileName}' could not be returned to the inbox and stays claimed under "
            + ".processing until the next start; the failure that caused the retry is reported "
            + "separately")]
    private static partial void RestoreFailed(ILogger logger, Exception exception, string fileName);
}

/// <summary>A file in the inbox whose producer has not said it is finished.</summary>
/// <param name="message">What the published name would be, and what a producer has to do.</param>
public sealed class FileDropNotPublishedException(string message) : Exception(message);
