using System.Globalization;
using System.Text;
using Nvm.Ingestion.Persistence;
using Nvm.Ingestion.RawCurves;
using Nvm.Kernel.Identity;

namespace Nvm.Ingestion.FileDrop;

/// <summary>Biến một file được drop vào thành các measurement đã lưu, và xếp phần còn lại.</summary>
/// <remarks>
/// Tách khỏi polling loop để toàn bộ quyết định — parse, ingest, move, giải thích — có thể được test
/// trên một thư mục thật mà không cần host, không cần timer, không cần chờ đợi.
/// </remarks>
public sealed partial class FileDropProcessor
{
    /// <summary>Được nêu tên trong mọi dòng archive mà adapter này ghi, thay vì một service account.</summary>
    private const string ArchiveActor = "ingestion:file-drop";

    private readonly CsvMeasurementReader _reader;
    private readonly IMeasurementIngestor _ingestor;
    private readonly FileDropOptions _options;
    private readonly TimeProvider _clock;
    private readonly IRawCurveArchive? _archive;
    private readonly ILogger<FileDropProcessor> _logger;

    private string ProcessingPath => Path.Combine(_options.InboxPath, ".processing");

    /// <summary>Tạo processor trên con đường dedup duy nhất.</summary>
    /// <param name="reader">Parser CSV.</param>
    /// <param name="ingestor">Cùng một ingestor mà đường MQTT dùng (C15.1).</param>
    /// <param name="options">Thư mục và timing.</param>
    /// <param name="clock">Đóng dấu <c>gateway_timestamp</c> là thời điểm file được đọc (K1).</param>
    /// <param name="logger">Structured log sink.</param>
    /// <param name="archive">
    /// Nơi giữ các byte gốc, hoặc null khi không cấu hình WORM store nào. Một file có measurement sẽ
    /// fail closed khi cái này là null: một nhà máy lưu measurement rồi vứt export đi thì không có
    /// câu trả lời cho "máy thực sự đã ghi ra cái gì" (C12.1).
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

    /// <summary>Tạo inbox và hai thư mục kết quả của nó.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(_options.InboxPath);
        Directory.CreateDirectory(_options.ProcessedPath);
        Directory.CreateDirectory(_options.RejectedPath);
        Directory.CreateDirectory(ProcessingPath);

        RecoverAbandonedClaims();
    }

    /// <summary>Xử lý một file và chuyển nó ra khỏi inbox.</summary>
    /// <param name="path">File cần đọc.</param>
    /// <param name="cancellationToken">Dừng công việc khi host tắt.</param>
    /// <returns>Những gì đã được lưu, hoặc null khi toàn bộ file bị từ chối.</returns>
    public async Task<IngestionResult?> ProcessAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var name = ExportName(Path.GetFileName(path));
        var claim = Claim(path, name);

        try
        {
            // Chỉ đọc một lần sau atomic claim. Parsing, WORM archival và bản sao kết quả đều dùng
            // chung một buffer bất biến này. Mở lại inbox path hoặc file đã claim sẽ để một exporter
            // append/thay byte giữa các giai đoạn đó và tạo ra telemetry từ A với một bản gốc B bị
            // gán ghép.
            var snapshot = await File.ReadAllBytesAsync(claim.SnapshotPath, cancellationToken);
            return await ProcessSnapshotAsync(claim, snapshot, name, cancellationToken);
        }
        catch
        {
            // Cancellation không hủy recovery. Mất bản gốc đã claim trong lúc host đang dừng lại còn
            // tệ hơn là mất thêm chút thời gian để dừng.
            if (File.Exists(claim.SnapshotPath))
            {
                try
                {
                    // File đã claim chính là snapshot: không gì ghi vào `.processing`, nên các byte
                    // đọc được lúc nãy và các byte trên đĩa là cùng một dữ liệu. Chuyển file đó trở
                    // lại chỉ là một rename, trong khi ghi lại buffer ra lần nữa sẽ là một create và
                    // một rename với một khoảng hở ở giữa cho một crash dừng lại trong đó.
                    RepublishClaim(claim, "retry");
                }
                catch (Exception restoreFailure)
                {
                    // Một restore thất bại không được phép thay thế thất bại đã gây ra nó. Throw từ
                    // đây sẽ báo cáo cái tên trong inbox là vấn đề và vứt bỏ nguyên nhân thật -- kiểu
                    // archive không tiếp cận được, transaction rollback -- trong khi để claim nằm
                    // dưới `.processing` cho tới lần khởi động lại kế tiếp. Cả hai đáng một dòng log;
                    // chỉ một cái đáng một exception, và đó là cái đang trên đường thoát ra ngoài rồi.
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
            // Toàn bộ file, không phải một dòng. Không gì trong nó có thể được attribute, nên không
            // gì trong nó được lưu.
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

        // Mọi dòng dữ liệu, không chỉ những dòng parse trót lọt tới cùng. Một dòng có thể fail ở
        // VALUE của nó mà vẫn nêu tên máy của nó hoàn toàn rõ ràng, và chỉ đếm những parse thành công
        // đã để lọt đúng loại file đó qua: dòng của máy A được lưu, cả file A+B được archive dưới A,
        // và file được xếp vào processed. Một audit độc lập phát hiện điều này vào 2026-09-01, sau
        // khi vòng trước đó đã từng "đóng" lỗ hổng mixed-machine một lần rồi.
        var machines = parsed.Measurements
            .Select(measurement => measurement.EquipmentPath)
            .Concat(parsed.Rejected.Where(line => line.Identity is not null).Select(line => line.Identity!))
            .Distinct()
            .ToList();

        // Một dòng mà identity của nó hoàn toàn không đọc được thì còn tệ hơn một máy thứ hai: nó có
        // thể CHÍNH LÀ một máy thứ hai và không có cách nào để biết. File khi đó không thể chứng
        // minh là thuộc về ai, và một archive được xếp dưới một máy có thể không sở hữu mọi dòng
        // trong đó thì không phải bằng chứng. Đây là một sự khác biệt cố ý so với "một dòng hỏng
        // không làm từ chối cả file" trong CsvMeasurementReader: một VALUE hỏng tốn một measurement,
        // một IDENTITY hỏng tốn khả năng nói được đây là file của ai.
        var unattributable = parsed.Rejected.Count(line => line.Identity is null);
        var hasDataLines = parsed.Measurements.Count > 0 || parsed.Rejected.Count > 0;

        // Một CSV hợp lệ về cú pháp nhưng không có reading nào không phải một no-op thành công. Nó
        // không thể được attribute cho một máy, nên nó không thể có bản gốc bất biến mà `processed`
        // hứa hẹn. Quan trọng hơn, coi nó là thành công sẽ che giấu một thất bại bình thường của nhà
        // máy, khi một exporter phát ra một header sau khi đã mất run mà lẽ ra nó phải export.
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

        // Bị từ chối trước khi bất cứ gì được lưu, và bị từ chối bất kể có cấu hình archive hay
        // không. Một export là bản ghi của một máy cho một run; một file nêu tên nhiều máy không có
        // câu trả lời duy nhất cho "curve này của ai", nên nó không thể được archive như bản gốc của
        // bất kỳ ai — và một file không bao giờ có được một bản gốc thì không được phép thành công.
        // Phiên bản trước đó của đoạn này đã lưu các dòng, log một cảnh báo và xếp file vào
        // `processed`, chính là thất bại mà toàn bộ adapter này tồn tại để tránh: một thứ trông như
        // đã được xử lý mà thực ra không phải.
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

        // Một file CÓ dòng dữ liệu nhưng không tạo ra measurement nào thì không thuộc về `processed`.
        //
        // Không gì được lưu, nên không gì thiếu bản gốc và K4 không bị vi phạm — nhưng export vẫn
        // rời khỏi inbox và rơi vào thư mục mà cái tên của nó nói rằng nội dung đã đi vào thành công.
        // Một operator đọc `processed` không thể phân biệt file này với một file đã thành công, và
        // artefact rejected-lines giải thích nó lại nằm ở một thư mục khác. Tín hiệu và kết quả chỉ
        // theo hai hướng ngược nhau.
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

        // Trước bản sao kết quả, và sau khi các dòng đã được lưu. Nếu archive không tiếp cận được thì
        // đúng snapshot đã claim được khôi phục lại inbox và lần poll kế tiếp sẽ thử lại: các dòng đã
        // vào rồi, nên retry chỉ tốn một vòng deduplication và đổi lại đảm bảo rằng không file nào
        // tới được `processed` mà thiếu byte gốc của nó. Cả lúc Program khởi động lẫn guard bên dưới
        // đều fail closed khi file drop không có archive.
        await ArchiveOriginalAsync(snapshot, name, descriptor, cancellationToken);

        // Chỉ publish sau khi transaction đã commit. Thứ tự ngược lại sẽ để lại một khoảng hở nơi
        // file được đánh dấu processed mà không gì được lưu, và một crash bên trong khoảng đó sẽ làm
        // mất export một cách âm thầm. Publish trễ chỉ có thể lặp lại một snapshot, và lặp lại chính
        // là thứ dedup sinh ra để xử lý.
        await CompleteSnapshotAsync(
            claim,
            snapshot,
            Path.Combine(_options.ProcessedPath, name),
            cancellationToken);

        FileProcessed(_logger, name, result.Inserted, result.Duplicates, parsed.Rejected.Count);
        return result;
    }

    /// <summary>Giữ đúng các byte của một export bên cạnh các dòng đọc ra từ nó.</summary>
    /// <remarks>
    /// Interval tới từ các measurement chứ không phải từ tên file, và điểm kết thúc của nó là
    /// exclusive: interval archive là half-open giống như mọi interval khác trong hệ thống này, nên
    /// mẫu cuối cùng nằm bên trong nó còn mẫu đầu tiên của export kế tiếp thì không. Một file không
    /// có descriptor không bao giờ tới được đây — <see cref="ProcessAsync"/> đã từ chối nó trước khi
    /// bất cứ gì được lưu.
    /// </remarks>
    private async Task ArchiveOriginalAsync(
        byte[] snapshot,
        string name,
        RawCurveDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (_archive is null)
        {
            // KHÔNG PHẢI kiểu warning-rồi-tiếp-tục. Chuyển file vào `processed` ở đây đã tiêu thụ
            // export và phá hủy bản sao byte duy nhất mà một auditor có quyền hỏi tới, đúng chính là
            // kết quả mà C12.1 tồn tại để ngăn chặn. Comment từng nằm trên nhánh này mô tả lỗ hổng
            // thay vì đóng nó lại.
            //
            // Program.cs từ chối khởi động khi file drop được bật mà không có archive, nên một
            // service được deploy đúng cách không bao giờ chạm tới dòng này. Nó vẫn ở đây như lớp
            // khóa thứ hai, vì lớp thứ nhất chỉ bảo vệ composition root và class này cũng được
            // construct trực tiếp bởi test và bởi bất cứ thứ gì viết sau này.
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

    /// <summary>Máy duy nhất và interval mà một file bao phủ, hoặc null khi nó bao phủ nhiều máy.</summary>
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
                // Nhiều cell đi qua một channel trong một export là chuyện bình thường. File vẫn là
                // bản gốc của một máy, nên nó được archive — không nêu tên unit nào, thay vì nêu tên
                // cell đầu tiên, việc đó sẽ xếp cả export dưới serial của một cell duy nhất.
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
            // Một micro giây, không phải một tick: interval được lưu trong một `timestamptz`, và một
            // điểm kết thúc chỉ tồn tại ở độ phân giải 100 ns là một điểm kết thúc mà archive không
            // giữ được. Một file chỉ có một reading là trường hợp chứng minh điều đó -- kết thúc một
            // tick sau khi bắt đầu, nó lưu thành một interval bằng không và PostgreSQL đã từ chối dòng đó.
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

            // Header đi kèm theo nó. Một dòng bị từ chối đứng một mình là một hàng dấu phẩy mà một
            // operator phải giải mã bằng cách đếm; có header ở trên, nó đọc được ngay.
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
            // Một rename lấy trọn cả export, vì export là một file duy nhất. Phiên bản trước đó
            // publish readiness bằng một file thứ hai bên cạnh dữ liệu phải thực hiện hai rename theo
            // thứ tự, và một producer publish lại cùng tên đó giữa hai lần rename sẽ giao cho reader
            // các byte của một export dưới readiness của một export khác -- đo được, không phải suy
            // đoán (ADR-035 §Evidence, probe `PAIR_RACE`). Không thứ tự nào của hai rename đóng được
            // lỗ hổng đó, nên chỉ có một rename: suffix mà producer đổi tên file thành chính là
            // readiness, và lấy file đó lấy luôn cả readiness đi cùng.
            File.Move(publishedPath, snapshotPath);
            return new ClaimedFile(snapshotPath, directoryPath);
        }
        catch
        {
            DeleteEmptyClaimDirectory(directoryPath);
            throw;
        }
    }

    /// <summary>Tên riêng của export, từ chối bất cứ thứ gì mà producer chưa publish.</summary>
    /// <remarks>
    /// Kiểm tra ở đây thay vì chỉ trong watcher vì đây là claim boundary: một caller giao một file
    /// viết dở phải bị từ chối bởi chính cái thứ đáng ra sẽ lưu nó.
    /// </remarks>
    private string ExportName(string publishedName)
    {
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
        dataFilePath + _options.PublishedSuffix;

    /// <summary>Publish một export đã claim trở lại inbox dưới một cái tên của riêng nó.</summary>
    /// <remarks>
    /// Không bao giờ dùng cái tên mà nó đã tới với. Một POSIX rename thay thế bất cứ gì đang nằm ở
    /// đích, nên một producer publish lại cùng tên đó không thể bị chặn bởi bất kỳ kiểm tra nào thực
    /// hiện ở phía này -- một exclusive create chỉ là một placeholder, và `mv` đi xuyên thẳng qua một
    /// placeholder (ADR-035 §Evidence, probe `RESERVATION_RACE`). Cách duy nhất để không phá hủy
    /// export của người khác là không nhắm vào một cái tên mà người khác có thể dùng. GUID cũng nói
    /// cho một operator đang đọc inbox biết rằng file này đã đi vòng qua loop một lần rồi.
    /// </remarks>
    private void RepublishClaim(ClaimedFile claim, string reason)
    {
        var target = PublishedPath(
            Path.Combine(
                _options.InboxPath,
                UniqueName(Path.GetFileName(claim.SnapshotPath), reason)));

        // Lại một rename duy nhất: sau đó export nằm trong inbox và đã publish, hoặc nó vẫn còn được
        // claim và có thể phục hồi. Không có trạng thái thứ ba nào cho một crash dừng lại trong đó.
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

            // Overwrite, và chỉ bao giờ ở bên trong các thư mục kết quả của riêng adapter này, nơi
            // re-export một tên file là chuyện bình thường và archive bất biến vốn dĩ đã
            // content-addressed rồi. Không gì ghi vào inbox qua con đường này: một file quay lại đó
            // đi bằng rename, dưới một cái tên không producer nào sẽ chọn.
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

    /// <summary>Đặt tên một export được trả lại theo cái nó bắt nguồn, cộng thêm nó đã qua bao nhiêu vòng.</summary>
    /// <remarks>
    /// Marker thay thế marker trước đó thay vì chồng lên trên nó. Nối thêm vào bất cứ tên nào nó
    /// nhận được khiến mỗi vòng của retry loop dài thêm ~40 byte, và sau sáu vòng cái tên vượt qua
    /// giới hạn 255 byte: move thất bại, và export bị mắc kẹt dưới `.processing` với chỉ một dòng
    /// log để nói lên điều đó. Phát hiện bởi `scripts/file-drop-race-probe.sh` trên stack đang chạy,
    /// nơi một export không bao giờ archive được cứ đi vòng vòng trong loop cho tới khi nó không thể
    /// di chuyển được nữa.
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

    /// <summary>Đọc một marker mà adapter này đã ghi, và không gì khác.</summary>
    /// <remarks>
    /// Chỉ <c>retry</c> và <c>recovered</c> được nhận diện, nên một producer có tên file của riêng
    /// nó kết thúc bằng thứ-gì-đó-hex vẫn giữ nguyên mọi phần của cái tên mà nó đã chọn.
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

/// <summary>Một file trong inbox mà producer của nó chưa nói là đã xong.</summary>
/// <param name="message">Tên đã publish lẽ ra sẽ là gì, và producer phải làm gì.</param>
public sealed class FileDropNotPublishedException(string message) : Exception(message);
