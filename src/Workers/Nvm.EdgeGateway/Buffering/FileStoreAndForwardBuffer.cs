using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Một hàng đợi append-only chia segment, với read cursor được fsync riêng.</summary>
/// <remarks>
/// Data record có dạng <c>[length:int32 little-endian][payload][crc32]</c>. Phần đuôi bị cắt cụt do
/// mất điện sẽ bị truncate về ranh giới record hoàn chỉnh liền trước. Một record đầy đủ nhưng CRC sai
/// vẫn bị đi qua (crossed) nhưng không bao giờ được trả về. Cursor chỉ được append sau khi ingestion
/// chấp nhận một batch, nên replay là khả thi, và gửi trùng vẫn thà hơn là mất dữ liệu.
/// </remarks>
public sealed class FileStoreAndForwardBuffer : IAsyncDisposable
{
    /// <summary>Số byte của length cộng CRC bao quanh mỗi payload.</summary>
    public const int RecordOverhead = sizeof(int) + sizeof(uint);

    private const uint CursorMagic = 0x4e564d43; // NVMC
    private const int CursorVersion = 1;
    private const int CursorBodyBytes = sizeof(uint) + sizeof(int) + (sizeof(long) * 4);
    private const int CursorRecordBytes = CursorBodyBytes + sizeof(uint);
    private const string CursorFileName = "cursor.nvmc";
    private const string SegmentPrefix = "segment-";
    private const string SegmentSuffix = ".nvmq";

    private readonly PersistentBufferOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SortedDictionary<long, SegmentState> _segments = [];
    private readonly string _cursorPath;
    private FileStream _tailStream = null!;
    private BufferCheckpoint _head = null!;
    private BufferCheckpoint? _offeredCheckpoint;
    private long _cursorSequence;
    private long _depth;
    private long _bytes;
    private long _corruptRecords;
    private long _truncatedTails;
    private long _dataFsyncs;
    private bool _disposed;

    /// <summary>Mở một hàng đợi đã tồn tại, hoặc tạo segment đầu tiên rỗng.</summary>
    public FileStoreAndForwardBuffer(PersistentBufferOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        Directory.CreateDirectory(options.DirectoryPath);
        _cursorPath = Path.Combine(options.DirectoryPath, CursorFileName);

        RecoverSegments();
        RecoverCursor();
        NormalizeRecoveredHead();
        CountDepthFromHead();

        var tail = _segments.Last();
        _tailStream = OpenSegmentForAppend(tail.Value.Path);
        _tailStream.Position = tail.Value.ValidLength;
    }

    /// <summary>Depth hiện tại, số byte trên đĩa và các bộ đếm phục hồi (recovery).</summary>
    public BufferSnapshot Snapshot =>
        new(
            Interlocked.Read(ref _depth),
            Interlocked.Read(ref _bytes),
            Interlocked.Read(ref _corruptRecords),
            Interlocked.Read(ref _truncatedTails),
            Interlocked.Read(ref _dataFsyncs),
            _options.MaxBytes);

    /// <summary>Append một batch cho một lần fsync. Phương thức chỉ trả về sau khi dữ liệu đã tới đĩa.</summary>
    /// <remarks>
    /// Cả batch được kiểm tra capacity trước khi byte đầu tiên được ghi. Vì vậy vượt trần cứng sẽ
    /// không bao giờ tạo ra chấp nhận một phần (partial acceptance), và không bao giờ ghi đè lên
    /// một record cũ hơn.
    /// </remarks>
    public async ValueTask AppendBatchAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> payloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        if (payloads.Count == 0)
        {
            return;
        }

        var framedBytes = 0L;

        foreach (var payload in payloads)
        {
            if (payload.Length == 0 || payload.Length > _options.MaxRecordBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payloads),
                    payload.Length,
                    $"Each buffer payload must contain 1..{_options.MaxRecordBytes} bytes.");
            }

            framedBytes = checked(framedBytes + payload.Length + RecordOverhead);
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            ThrowIfDisposed();

            if (_bytes + framedBytes > _options.MaxBytes)
            {
                throw new BufferCapacityExceededException(_bytes, framedBytes, _options.MaxBytes);
            }

            foreach (var payload in payloads)
            {
                await RotateIfNeededAsync(payload.Length + RecordOverhead, cancellationToken);
                await WriteRecordAsync(_tailStream, payload, cancellationToken);

                var tailId = _segments.Last().Key;
                var state = _segments[tailId];
                _segments[tailId] = state with
                {
                    ValidLength = _tailStream.Position,
                    RecordCount = state.RecordCount + 1,
                };

                _bytes += payload.Length + RecordOverhead;
                _depth++;
            }

            // FlushAsync chỉ chuyển managed buffer sang OS. Flush(true) mới là ranh giới bền
            // (durability boundary) mà MQTT acknowledgement chờ đợi; không có API fsync bất đồng bộ.
            await _tailStream.FlushAsync(cancellationToken);
            DurableDataFlush(_tailStream);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Đọc mà không làm dịch cursor bền (durable cursor).</summary>
    public async ValueTask<BufferedBatch> ReadBatchAsync(
        int maxRecords,
        int maxPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            ThrowIfDisposed();

            if (_offeredCheckpoint is not null)
            {
                throw new InvalidOperationException("The previous buffer batch must be acknowledged before another read.");
            }

            var payloads = ImmutableArray.CreateBuilder<byte[]>();
            var position = _head;
            var traversed = 0;
            var payloadBytes = 0;
            FileStream? readStream = null;
            var openSegmentId = 0L;

            try
            {
                while (traversed < maxRecords)
                {
                    if (!TryNormalizePosition(position, out position))
                    {
                        break;
                    }

                    var segment = _segments[position.SegmentId];

                    if (position.Offset >= segment.ValidLength)
                    {
                        break;
                    }

                    if (readStream is null || openSegmentId != position.SegmentId)
                    {
                        if (readStream is not null)
                        {
                            await readStream.DisposeAsync();
                        }

                        readStream = OpenSegmentForRead(segment.Path);
                        openSegmentId = position.SegmentId;
                    }

                    var record = await ReadRecordAsync(readStream, position.Offset, cancellationToken)
                        ?? throw new IOException(
                            $"Recovered segment {position.SegmentId} ended before its recorded valid length "
                            + $"{segment.ValidLength} at offset {position.Offset}.");

                    if (record.CrcMatches
                        && payloads.Count > 0
                        && payloadBytes + record.Payload.Length > maxPayloadBytes)
                    {
                        break;
                    }

                    traversed++;
                    position = position with
                    {
                        Offset = record.EndOffset,
                        AcknowledgedRecords = position.AcknowledgedRecords + 1,
                    };

                    if (record.CrcMatches)
                    {
                        payloads.Add(record.Payload);
                        payloadBytes += record.Payload.Length;
                    }
                }
            }
            finally
            {
                if (readStream is not null)
                {
                    await readStream.DisposeAsync();
                }
            }

            // Dịch checkpoint đang nằm đúng tại cuối một segment sang segment kế tiếp. Nhờ vậy file
            // cũ có thể bị xoá sau khi cursor được fsync, trong khi một tail đang hoạt động mà rỗng
            // thì vẫn giữ nguyên tại chỗ.
            TryNormalizePosition(position, out position);

            if (traversed == 0)
            {
                return BufferedBatch.Empty(_head);
            }

            _offeredCheckpoint = position;
            return new BufferedBatch(payloads.DrainToImmutable(), position, traversed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Dịch cursor sau khi toàn bộ payload hợp lệ trong một batch đã đề xuất được chấp nhận.</summary>
    public async ValueTask AcknowledgeAsync(
        BufferedBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (!batch.HasProgress)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            ThrowIfDisposed();

            if (_offeredCheckpoint != batch.Checkpoint)
            {
                throw new InvalidOperationException(
                    "Only the checkpoint returned by the outstanding read may be acknowledged.");
            }

            await RollDrainedTailIfUnwritableAsync(batch.Checkpoint, cancellationToken);
            TryNormalizePosition(batch.Checkpoint, out var durableCheckpoint);
            PersistCursor(durableCheckpoint);
            _head = durableCheckpoint;
            _offeredCheckpoint = null;
            _depth = Math.Max(0, _depth - batch.RecordsTraversed);
            DeleteSegmentsBeforeHead();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Giải phóng file handle. Tính đúng đắn không bao giờ được phép phụ thuộc vào việc đường này có được gọi hay không.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();

        try
        {
            if (_disposed)
            {
                return;
            }

            await _tailStream.DisposeAsync();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void RecoverSegments()
    {
        foreach (var path in Directory.EnumerateFiles(_options.DirectoryPath, SegmentPrefix + "*" + SegmentSuffix))
        {
            if (!TryParseSegmentId(path, out var id))
            {
                continue;
            }

            var state = ScanAndRepairSegment(id, path);
            _segments.Add(id, state);
            _bytes += state.ValidLength;
            _corruptRecords += state.CorruptRecords;
        }

        if (_segments.Count == 0)
        {
            const long firstId = 1;
            var path = SegmentPath(firstId);
            using (File.Create(path))
            {
            }

            _segments.Add(firstId, new SegmentState(path, 0, 0, 0));
        }
    }

    private SegmentState ScanAndRepairSegment(long id, string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var offset = 0L;
        var records = 0L;
        var corrupt = 0L;
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        Span<byte> crcBytes = stackalloc byte[sizeof(uint)];

        while (offset < stream.Length)
        {
            stream.Position = offset;

            if (!TryReadExactly(stream, lengthBytes))
            {
                TruncateTail(stream, offset);
                break;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);

            if (length <= 0 || length > _options.MaxRecordBytes)
            {
                TruncateTail(stream, offset);
                break;
            }

            var end = checked(offset + RecordOverhead + length);

            if (end > stream.Length)
            {
                TruncateTail(stream, offset);
                break;
            }

            var payload = new byte[length];
            stream.ReadExactly(payload);
            stream.ReadExactly(crcBytes);

            if (BinaryPrimitives.ReadUInt32LittleEndian(crcBytes) != Crc32.Compute(payload))
            {
                corrupt++;
            }

            offset = end;
            records++;
        }

        return new SegmentState(path, offset, records, corrupt);
    }

    private void RecoverCursor()
    {
        if (!File.Exists(_cursorPath))
        {
            var firstSegment = _segments.First();
            _head = new BufferCheckpoint(firstSegment.Key, 0, 0);
            return;
        }

        using var stream = new FileStream(_cursorPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var goodLength = 0L;
        BufferCheckpoint? latest = null;
        Span<byte> record = stackalloc byte[CursorRecordBytes];

        while (stream.Position < stream.Length)
        {
            if (!TryReadExactly(stream, record))
            {
                break;
            }

            var body = record[..CursorBodyBytes];
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(record[CursorBodyBytes..]);

            if (BinaryPrimitives.ReadUInt32LittleEndian(body) != CursorMagic
                || BinaryPrimitives.ReadInt32LittleEndian(body[sizeof(uint)..]) != CursorVersion
                || Crc32.Compute(body) != expectedCrc)
            {
                break;
            }

            var sequence = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
            var segmentId = BinaryPrimitives.ReadInt64LittleEndian(body[16..]);
            var offset = BinaryPrimitives.ReadInt64LittleEndian(body[24..]);
            var acknowledged = BinaryPrimitives.ReadInt64LittleEndian(body[32..]);

            if (sequence <= _cursorSequence || segmentId <= 0 || offset < 0 || acknowledged < 0)
            {
                break;
            }

            _cursorSequence = sequence;
            latest = new BufferCheckpoint(segmentId, offset, acknowledged);
            goodLength = stream.Position;
        }

        if (stream.Length != goodLength)
        {
            stream.SetLength(goodLength);
            stream.Flush(flushToDisk: true);
            _truncatedTails++;
        }

        var first = _segments.First();
        _head = latest ?? new BufferCheckpoint(first.Key, 0, 0);
    }

    private void NormalizeRecoveredHead()
    {
        var first = _segments.First();
        var last = _segments.Last();

        if (_head.SegmentId < first.Key)
        {
            // Các segment nằm dưới cursor chỉ bị xoá sau khi cursor đã được fsync.
            _head = _head with { SegmentId = first.Key, Offset = 0 };
            return;
        }

        if (_head.SegmentId > last.Key
            || !_segments.TryGetValue(_head.SegmentId, out var segment)
            || _head.Offset > segment.ValidLength
            || !IsRecordBoundary(segment.Path, _head.Offset))
        {
            // Một cursor mà ta không chứng minh được là trỏ đúng giữa hai record thì bị bỏ qua theo
            // hướng an toàn (conservative). Replay lại thì có thể trùng; đoán bừa qua byte thì có
            // thể mất chúng. Journal cũng phải được reset: append sequence 1 sau một sequence hợp lệ
            // cao hơn sẽ đầu độc mọi lần restart về sau.
            _head = new BufferCheckpoint(first.Key, 0, 0);
            _cursorSequence = 0;
            ResetCursorJournal();
        }

        TryNormalizePosition(_head, out _head);
    }

    private void CountDepthFromHead()
    {
        var position = _head;

        foreach (var (id, segment) in _segments.Where(pair => pair.Key >= position.SegmentId))
        {
            var offset = id == position.SegmentId ? position.Offset : 0;
            _depth += CountRecords(segment.Path, offset, segment.ValidLength);
        }
    }

    private async ValueTask RotateIfNeededAsync(int framedLength, CancellationToken cancellationToken)
    {
        if (_tailStream.Length == 0 || _tailStream.Length + framedLength <= _options.SegmentBytes)
        {
            return;
        }

        await _tailStream.FlushAsync(cancellationToken);
        DurableDataFlush(_tailStream);
        await _tailStream.DisposeAsync();

        var nextId = checked(_segments.Last().Key + 1);
        var nextPath = SegmentPath(nextId);
        _tailStream = OpenSegmentForAppend(nextPath);
        _segments.Add(nextId, new SegmentState(nextPath, 0, 0, 0));
    }

    private async ValueTask RollDrainedTailIfUnwritableAsync(
        BufferCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var tail = _segments.Last();

        if (checkpoint.SegmentId != tail.Key
            || checkpoint.Offset != tail.Value.ValidLength
            || tail.Value.ValidLength + RecordOverhead < _options.MaxBytes)
        {
            return;
        }

        // Cấu hình chỉ một segment có thể lấp đầy đúng tới trần. Một khi mọi record đã được chấp
        // nhận, tạo một tail rỗng mới trước khi persist cursor; nếu không thì không append nào có
        // thể kích hoạt rotation bình thường, và capacity sẽ bị treo (paused) vĩnh viễn.
        await _tailStream.FlushAsync(cancellationToken);
        DurableDataFlush(_tailStream);
        await _tailStream.DisposeAsync();

        var nextId = checked(tail.Key + 1);
        var nextPath = SegmentPath(nextId);
        _tailStream = OpenSegmentForAppend(nextPath);
        _segments.Add(nextId, new SegmentState(nextPath, 0, 0, 0));
    }

    private static async ValueTask WriteRecordAsync(
        FileStream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        var trailer = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, Crc32.Compute(payload.Span));

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.WriteAsync(trailer, cancellationToken);
    }

    private async ValueTask<RecordRead?> ReadRecordAsync(
        FileStream stream,
        long offset,
        CancellationToken cancellationToken)
    {
        stream.Position = offset;

        var header = new byte[sizeof(int)];

        if (!await TryReadExactlyAsync(stream, header, cancellationToken))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);

        if (length <= 0 || length > _options.MaxRecordBytes)
        {
            return null;
        }

        var payload = new byte[length];
        var trailer = new byte[sizeof(uint)];

        if (!await TryReadExactlyAsync(stream, payload, cancellationToken)
            || !await TryReadExactlyAsync(stream, trailer, cancellationToken))
        {
            return null;
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        return new RecordRead(payload, offset + RecordOverhead + length, expected == Crc32.Compute(payload));
    }

    private void PersistCursor(BufferCheckpoint checkpoint)
    {
        Span<byte> record = stackalloc byte[CursorRecordBytes];
        var body = record[..CursorBodyBytes];

        BinaryPrimitives.WriteUInt32LittleEndian(body, CursorMagic);
        BinaryPrimitives.WriteInt32LittleEndian(body[sizeof(uint)..], CursorVersion);
        BinaryPrimitives.WriteInt64LittleEndian(body[8..], ++_cursorSequence);
        BinaryPrimitives.WriteInt64LittleEndian(body[16..], checkpoint.SegmentId);
        BinaryPrimitives.WriteInt64LittleEndian(body[24..], checkpoint.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(body[32..], checkpoint.AcknowledgedRecords);
        BinaryPrimitives.WriteUInt32LittleEndian(record[CursorBodyBytes..], Crc32.Compute(body));

        using var stream = new FileStream(
            _cursorPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(record);
        stream.Flush(flushToDisk: true);
    }

    private void ResetCursorJournal()
    {
        using var stream = new FileStream(
            _cursorPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
    }

    private void DeleteSegmentsBeforeHead()
    {
        var obsolete = _segments.Keys.Where(id => id < _head.SegmentId).ToArray();

        foreach (var id in obsolete)
        {
            var segment = _segments[id];
            File.Delete(segment.Path);
            _segments.Remove(id);
            _bytes -= segment.ValidLength;
        }
    }

    private bool TryNormalizePosition(BufferCheckpoint input, out BufferCheckpoint normalized)
    {
        normalized = input;

        while (_segments.TryGetValue(normalized.SegmentId, out var segment)
            && normalized.Offset == segment.ValidLength)
        {
            var next = 0L;

            foreach (var id in _segments.Keys)
            {
                if (id > normalized.SegmentId)
                {
                    next = id;
                    break;
                }
            }

            if (next == 0)
            {
                return true;
            }

            normalized = normalized with { SegmentId = next, Offset = 0 };
        }

        return _segments.ContainsKey(normalized.SegmentId);
    }

    private bool IsRecordBoundary(string path, long target)
    {
        if (target == 0)
        {
            return true;
        }

        using var stream = File.OpenRead(path);
        var offset = 0L;
        Span<byte> header = stackalloc byte[sizeof(int)];

        while (offset < target && TryReadExactly(stream, header))
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);

            if (length <= 0 || length > _options.MaxRecordBytes)
            {
                return false;
            }

            offset = checked(offset + RecordOverhead + length);
            stream.Position = offset;
        }

        return offset == target;
    }

    private long CountRecords(string path, long start, long end)
    {
        using var stream = File.OpenRead(path);
        var offset = start;
        var count = 0L;
        Span<byte> header = stackalloc byte[sizeof(int)];

        while (offset < end)
        {
            stream.Position = offset;
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            offset = checked(offset + RecordOverhead + length);
            count++;
        }

        return count;
    }

    private void TruncateTail(FileStream stream, long validLength)
    {
        stream.SetLength(validLength);
        stream.Flush(flushToDisk: true);
        _truncatedTails++;
    }

    private string SegmentPath(long id) =>
        Path.Combine(
            _options.DirectoryPath,
            SegmentPrefix + id.ToString("D20", CultureInfo.InvariantCulture) + SegmentSuffix);

    private static bool TryParseSegmentId(string path, out long id)
    {
        var name = Path.GetFileName(path);
        var text = name[SegmentPrefix.Length..^SegmentSuffix.Length];
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    private static FileStream OpenSegmentForAppend(string path) =>
        new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);

    private static FileStream OpenSegmentForRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    // Stream.FlushAsync không có phiên bản tương đương flush-xuống-đĩa-vật-lý. Caller await nó
    // trước để làm rỗng managed buffer, rồi helper hẹp này mới gọi tới nguyên hàm bền (durability
    // primitive) mà MQTT ACK phụ thuộc vào.
    private void DurableDataFlush(FileStream stream)
    {
        stream.Flush(flushToDisk: true);
        Interlocked.Increment(ref _dataFsyncs);
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var received = stream.Read(buffer[read..]);

            if (received == 0)
            {
                return false;
            }

            read += received;
        }

        return true;
    }

    private static async ValueTask<bool> TryReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var received = await stream.ReadAsync(buffer[read..], cancellationToken);

            if (received == 0)
            {
                return false;
            }

            read += received;
        }

        return true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record SegmentState(string Path, long ValidLength, long RecordCount, long CorruptRecords);

    private sealed record RecordRead(byte[] Payload, long EndOffset, bool CrcMatches);
}
