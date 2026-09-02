using System.Globalization;
using System.Text;
using Nvm.EdgeGateway.Buffering;

namespace Nvm.EdgeGateway;

/// <summary>Ghi một snapshot tiến trình chính xác, nguyên tử, phục vụ các lab vận hành fail-closed.</summary>
/// <remarks>
/// Log tiến độ được lấy mẫu (sampled) nên không thể chứng minh rằng batch cuối cùng đã xả cạn
/// xong. File này được cố tình đặt cục bộ trên volume của gateway: nó không thêm route OT/IT nào
/// và không thêm nền tảng observability nào. Một reader luôn thấy hoặc là snapshot hoàn chỉnh
/// trước đó, hoặc là snapshot tiếp theo, không bao giờ thấy một hỗn hợp ghi dở dang.
/// </remarks>
public sealed partial class GatewayDiagnosticsWriter : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly GatewayCounters _counters;
    private readonly FileStoreAndForwardBuffer _buffer;
    private readonly TimeProvider _clock;
    private readonly string _path;
    private readonly ILogger<GatewayDiagnosticsWriter> _logger;

    /// <summary>Tạo writer trên cùng bộ counter và buffer mà gateway đang sử dụng.</summary>
    public GatewayDiagnosticsWriter(
        GatewayCounters counters,
        FileStoreAndForwardBuffer buffer,
        EdgeGatewayOptions options,
        TimeProvider clock,
        ILogger<GatewayDiagnosticsWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _counters = counters;
        _buffer = buffer;
        _clock = clock;
        _path = options.ResolvedDiagnosticsPath;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");

        using var timer = new PeriodicTimer(Interval, _clock);

        try
        {
            do
            {
                await WriteAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown bình thường. Snapshot hoàn chỉnh cuối cùng vẫn đọc được.
        }
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        var snapshot = GatewayDiagnosticsSnapshot.Capture(_counters, _buffer.Snapshot, _clock.GetUtcNow());
        var temporaryPath = _path + ".tmp";

        try
        {
            await File.WriteAllTextAsync(temporaryPath, snapshot.ToText(), Utf8WithoutBom, cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SnapshotWriteFailed(_logger, exception, _path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write gateway diagnostics snapshot to {Path}")]
    private static partial void SnapshotWriteFailed(ILogger logger, Exception exception, string path);
}

/// <summary>Các counter chính xác từ một tiến trình gateway tại một thời điểm.</summary>
public sealed record GatewayDiagnosticsSnapshot(
    DateTimeOffset CapturedAt,
    long Decoded,
    long Buffered,
    long Forwarded,
    long Rejected,
    long RebirthRequests,
    long BufferFullEvents,
    long ThrottledFlushes,
    long RateLimitedFlushes,
    long BufferDepth,
    long BufferBytes,
    long CorruptRecords,
    long TruncatedTails,
    long DataFsyncs,
    long FlushBatches)
{
    /// <summary>Chụp lại các counter và state durable mà không suy ra giá trị từ log đã lấy mẫu.</summary>
    public static GatewayDiagnosticsSnapshot Capture(
        GatewayCounters counters,
        BufferSnapshot buffer,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(buffer);

        return new GatewayDiagnosticsSnapshot(
            capturedAt,
            counters.DecodedMessages,
            counters.BufferedMessages,
            counters.ForwardedMessages,
            counters.RejectedMessages,
            counters.RebirthRequests,
            counters.BufferFullEvents,
            counters.ThrottledFlushes,
            counters.RateLimitedFlushes,
            buffer.Depth,
            buffer.Bytes,
            buffer.CorruptRecords,
            buffer.TruncatedTails,
            buffer.DataFsyncs,
            counters.FlushBatches);
    }

    /// <summary>Định dạng key-value ổn định, đọc được bởi shell tối giản có sẵn trong container.</summary>
    public string ToText() => string.Create(
        CultureInfo.InvariantCulture,
        $"captured_at={CapturedAt:O}\n"
        + $"decoded={Decoded}\n"
        + $"buffered={Buffered}\n"
        + $"forwarded={Forwarded}\n"
        + $"rejected={Rejected}\n"
        + $"rebirth_requests={RebirthRequests}\n"
        + $"buffer_full_events={BufferFullEvents}\n"
        + $"throttled_flushes={ThrottledFlushes}\n"
        + $"rate_limited_flushes={RateLimitedFlushes}\n"
        + $"buffer_depth={BufferDepth}\n"
        + $"buffer_bytes={BufferBytes}\n"
        + $"corrupt_records={CorruptRecords}\n"
        + $"truncated_tails={TruncatedTails}\n"
        + $"data_fsyncs={DataFsyncs}\n"
        + $"flush_batches={FlushBatches}\n");
}
