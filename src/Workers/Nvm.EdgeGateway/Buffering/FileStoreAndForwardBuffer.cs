using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>An append-only segmented queue with a separately fsynced read cursor.</summary>
/// <remarks>
/// Data records are <c>[length:int32 little-endian][payload][crc32]</c>. A tail cut by power loss is
/// truncated to the preceding record boundary. A full record whose CRC fails is crossed but never
/// returned. The cursor is appended only after ingestion accepts a batch, so replay is possible and
/// duplicate delivery is preferable to loss.
/// </remarks>
public sealed class FileStoreAndForwardBuffer : IAsyncDisposable
{
    /// <summary>Length plus CRC bytes around every payload.</summary>
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
    private bool _disposed;

    /// <summary>Opens an existing queue or creates an empty first segment.</summary>
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

    /// <summary>Current depth, disk bytes and recovery counters.</summary>
    public BufferSnapshot Snapshot =>
        new(
            Interlocked.Read(ref _depth),
            Interlocked.Read(ref _bytes),
            Interlocked.Read(ref _corruptRecords),
            Interlocked.Read(ref _truncatedTails),
            _options.MaxBytes);

    /// <summary>Appends one fsync batch. The method returns only after the data reaches disk.</summary>
    /// <remarks>
    /// The entire batch is capacity-checked before its first byte is written. Crossing the hard cap
    /// therefore produces no partial acceptance and never overwrites an older record.
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

            // FlushAsync only moves managed buffers to the OS. Flush(true) is the durability
            // boundary the MQTT acknowledgement waits for; there is no asynchronous fsync API.
            await _tailStream.FlushAsync(cancellationToken);
            DurableFlush(_tailStream);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads without advancing the durable cursor.</summary>
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

            // Move a checkpoint at an exact segment end onto the next segment. This lets the old
            // file be deleted after the cursor is fsynced, while an empty active tail stays put.
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

    /// <summary>Moves the cursor after all valid payloads in an offered batch were accepted.</summary>
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

    /// <summary>Releases file handles. Correctness must never depend on this path being called.</summary>
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
            // Segments below the cursor were deleted only after the cursor was fsynced.
            _head = _head with { SegmentId = first.Key, Offset = 0 };
            return;
        }

        if (_head.SegmentId > last.Key
            || !_segments.TryGetValue(_head.SegmentId, out var segment)
            || _head.Offset > segment.ValidLength
            || !IsRecordBoundary(segment.Path, _head.Offset))
        {
            // A cursor we cannot prove points between records is ignored conservatively. Replaying
            // can duplicate; guessing past bytes can lose them. The journal must also be reset:
            // appending sequence 1 after a higher valid sequence would poison every later restart.
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
        DurableFlush(_tailStream);
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

        // A one-segment configuration can fill exactly to its cap. Once every record is accepted,
        // make a new empty tail before persisting the cursor; otherwise no append can trigger normal
        // rotation and capacity would remain paused forever.
        await _tailStream.FlushAsync(cancellationToken);
        DurableFlush(_tailStream);
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

    // Stream.FlushAsync has no flush-to-physical-disk equivalent. Callers await it first to empty
    // managed buffers, then this narrow helper invokes the durability primitive MQTT ACK depends on.
    private static void DurableFlush(FileStream stream) => stream.Flush(flushToDisk: true);

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
