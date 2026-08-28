using System.Buffers.Binary;
using System.Text;
using Nvm.EdgeGateway.Buffering;

namespace Nvm.UnitTests.EdgeGateway;

public sealed class FileStoreAndForwardBufferTests
{
    [Fact]
    public async Task Reopen_TenThousandRecords_ReturnsEveryPayloadInOrder()
    {
        await using var directory = new BufferTestDirectory();
        var expected = Enumerable.Range(0, 10_000).Select(Payload).ToArray();

        await using (var writer = new FileStoreAndForwardBuffer(directory.Options(segmentBytes: 32 * 1024)))
        {
            await writer.AppendBatchAsync(
                expected.Select(bytes => (ReadOnlyMemory<byte>)bytes).ToArray(),
                TestContext.Current.CancellationToken);
            writer.Snapshot.Depth.ShouldBe(10_000);
        }

        await using var reopened = new FileStoreAndForwardBuffer(directory.Options(segmentBytes: 32 * 1024));
        var actual = await ReadAllAsync(reopened);

        actual.Count.ShouldBe(expected.Length);

        for (var index = 0; index < expected.Length; index++)
        {
            actual[index].ShouldBe(expected[index]);
        }
    }

    [Fact]
    public async Task Acknowledge_FsyncsCursorAndReopenStartsAfterAcceptedBatch()
    {
        await using var directory = new BufferTestDirectory();
        var options = directory.Options(segmentBytes: 256);

        await using (var firstRun = new FileStoreAndForwardBuffer(options))
        {
            await firstRun.AppendBatchAsync(
                Enumerable.Range(0, 20).Select(index => (ReadOnlyMemory<byte>)Payload(index)).ToArray(),
                TestContext.Current.CancellationToken);
            var accepted = await firstRun.ReadBatchAsync(7, 4096, TestContext.Current.CancellationToken);
            await firstRun.AcknowledgeAsync(accepted, TestContext.Current.CancellationToken);
        }

        await using var restarted = new FileStoreAndForwardBuffer(directory.Options(segmentBytes: 256));
        var remaining = await ReadAllAsync(restarted);

        remaining.Count.ShouldBe(13);
        remaining[0].ShouldBe(Payload(7));
        remaining[^1].ShouldBe(Payload(19));
    }

    [Fact]
    public async Task Recovery_CrcMismatch_SkipsAndCountsThatRecordWithoutHidingNeighbors()
    {
        await using var directory = new BufferTestDirectory();

        await using (var writer = new FileStoreAndForwardBuffer(directory.Options()))
        {
            await writer.AppendBatchAsync(
                new ReadOnlyMemory<byte>[] { Payload(1), Payload(2), Payload(3) },
                TestContext.Current.CancellationToken);
        }

        var segment = directory.SingleSegment();
        var firstLength = Payload(1).Length;
        var secondPayloadOffset = FileStoreAndForwardBuffer.RecordOverhead + firstLength + sizeof(int);

        using (var file = new FileStream(segment, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            file.Position = secondPayloadOffset + 2;
            var original = file.ReadByte();
            original.ShouldBeGreaterThanOrEqualTo(0);
            file.Position--;
            file.WriteByte((byte)(original ^ 0xff));
            await file.FlushAsync(TestContext.Current.CancellationToken);
        }

        await using var recovered = new FileStoreAndForwardBuffer(directory.Options());
        recovered.Snapshot.CorruptRecords.ShouldBe(1);

        var batch = await recovered.ReadBatchAsync(10, 4096, TestContext.Current.CancellationToken);
        batch.RecordsTraversed.ShouldBe(3);
        batch.Payloads.Length.ShouldBe(2);
        batch.Payloads[0].ShouldBe(Payload(1));
        batch.Payloads[1].ShouldBe(Payload(3));
        await recovered.AcknowledgeAsync(batch, TestContext.Current.CancellationToken);
        recovered.Snapshot.Depth.ShouldBe(0);
    }

    [Fact]
    public async Task Recovery_IncompleteTail_TruncatesToLastCompleteRecord()
    {
        await using var directory = new BufferTestDirectory();

        await using (var writer = new FileStoreAndForwardBuffer(directory.Options()))
        {
            await writer.AppendBatchAsync(
                new ReadOnlyMemory<byte>[] { Payload(1), Payload(2) },
                TestContext.Current.CancellationToken);
        }

        var segment = directory.SingleSegment();
        var validLength = new FileInfo(segment).Length;

        await using (var file = new FileStream(segment, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            var declared = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(declared, 100);
            await file.WriteAsync(declared, TestContext.Current.CancellationToken);
            await file.WriteAsync(new byte[17], TestContext.Current.CancellationToken);
            await file.FlushAsync(TestContext.Current.CancellationToken);
        }

        await using var recovered = new FileStoreAndForwardBuffer(directory.Options());

        recovered.Snapshot.TruncatedTails.ShouldBe(1);
        new FileInfo(segment).Length.ShouldBe(validLength);
        (await ReadAllAsync(recovered)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Append_WhenHardCapWouldBeCrossed_RefusesWholeBatchAndNeverOverwritesOldest()
    {
        await using var directory = new BufferTestDirectory();
        var first = Payload(1);
        var cap = first.Length + FileStoreAndForwardBuffer.RecordOverhead + 8;
        var options = directory.Options(
            segmentBytes: cap,
            maxBytes: cap);

        await using var buffer = new FileStoreAndForwardBuffer(options);
        await buffer.AppendBatchAsync([first], TestContext.Current.CancellationToken);

        var thrown = await Should.ThrowAsync<BufferCapacityExceededException>(() =>
            buffer.AppendBatchAsync([Payload(2)], TestContext.Current.CancellationToken).AsTask());

        thrown.BytesOnDisk.ShouldBe(first.Length + FileStoreAndForwardBuffer.RecordOverhead);
        buffer.Snapshot.Depth.ShouldBe(1);
        var retained = await buffer.ReadBatchAsync(10, 4096, TestContext.Current.CancellationToken);
        retained.Payloads.ShouldHaveSingleItem().ShouldBe(first);
        await buffer.AcknowledgeAsync(retained, TestContext.Current.CancellationToken);

        await buffer.AppendBatchAsync([Payload(2)], TestContext.Current.CancellationToken);
        var afterCapacityRecovered = await buffer.ReadBatchAsync(10, 4096, TestContext.Current.CancellationToken);
        afterCapacityRecovered.Payloads.ShouldHaveSingleItem().ShouldBe(Payload(2));
    }

    [Fact]
    public async Task Rotation_AfterCursorFsync_DeletesOnlyFullyAcknowledgedOldSegments()
    {
        await using var directory = new BufferTestDirectory();
        var options = directory.Options(segmentBytes: 80);
        await using var buffer = new FileStoreAndForwardBuffer(options);
        await buffer.AppendBatchAsync(
            Enumerable.Range(0, 12).Select(index => (ReadOnlyMemory<byte>)Payload(index)).ToArray(),
            TestContext.Current.CancellationToken);

        var before = directory.Segments().Length;
        before.ShouldBeGreaterThan(1);

        while (buffer.Snapshot.Depth > 0)
        {
            var batch = await buffer.ReadBatchAsync(4, 4096, TestContext.Current.CancellationToken);
            await buffer.AcknowledgeAsync(batch, TestContext.Current.CancellationToken);
        }

        directory.Segments().Length.ShouldBe(1);
    }

    [Fact]
    public async Task DataFsyncCounter_IncludesEverySegmentRotationAndFinalCommit()
    {
        await using var directory = new BufferTestDirectory();
        await using var buffer = new FileStoreAndForwardBuffer(directory.Options(segmentBytes: 80));

        await buffer.AppendBatchAsync(
            Enumerable.Range(0, 12).Select(index => (ReadOnlyMemory<byte>)Payload(index)).ToArray(),
            TestContext.Current.CancellationToken);

        // A full segment is durably closed before rotation; the final segment is committed once at
        // the end. Therefore one cross-segment append performs one data fsync per resulting segment.
        buffer.Snapshot.DataFsyncs.ShouldBe(directory.Segments().Length);
    }

    [Fact]
    public async Task RecordTrailer_KnownVector_IsStandardCrc32IsoHdlc()
    {
        // The public on-disk format is useless if writer and reader share the same wrong checksum.
        // 123456789 is the standard check vector for CRC-32/ISO-HDLC.
        await using var directory = new BufferTestDirectory();
        await using (var buffer = new FileStoreAndForwardBuffer(directory.Options()))
        {
            await buffer.AppendBatchAsync(
                [Encoding.ASCII.GetBytes("123456789")],
                TestContext.Current.CancellationToken);
        }

        var record = await File.ReadAllBytesAsync(directory.SingleSegment(), TestContext.Current.CancellationToken);
        BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(record.Length - sizeof(uint)))
            .ShouldBe(0xcbf43926u);
    }

    [Fact]
    public async Task Recovery_CursorPastRepairedData_ResetsJournalBeforeWritingNextSequence()
    {
        await using var directory = new BufferTestDirectory();
        var firstRecordLength = Payload(1).Length + FileStoreAndForwardBuffer.RecordOverhead;

        await using (var writer = new FileStoreAndForwardBuffer(directory.Options()))
        {
            await writer.AppendBatchAsync(
                new ReadOnlyMemory<byte>[] { Payload(1), Payload(2), Payload(3) },
                TestContext.Current.CancellationToken);
            var acknowledged = await writer.ReadBatchAsync(2, 4096, TestContext.Current.CancellationToken);
            await writer.AcknowledgeAsync(acknowledged, TestContext.Current.CancellationToken);
        }

        await using (var segment = new FileStream(
            directory.SingleSegment(),
            FileMode.Open,
            FileAccess.Write,
            FileShare.None))
        {
            segment.SetLength(firstRecordLength);
            await segment.FlushAsync(TestContext.Current.CancellationToken);
        }

        await using (var repaired = new FileStoreAndForwardBuffer(directory.Options()))
        {
            new FileInfo(directory.Cursor()).Length.ShouldBe(0);
            var replayed = await repaired.ReadBatchAsync(10, 4096, TestContext.Current.CancellationToken);
            replayed.Payloads.ShouldHaveSingleItem().ShouldBe(Payload(1));
            await repaired.AcknowledgeAsync(replayed, TestContext.Current.CancellationToken);
        }

        await using var restarted = new FileStoreAndForwardBuffer(directory.Options());
        restarted.Snapshot.Depth.ShouldBe(0);
    }

    private static byte[] Payload(int index) =>
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"message-{index:D5}-{new string('x', index % 31)}"));

    private static async Task<List<byte[]>> ReadAllAsync(FileStoreAndForwardBuffer buffer)
    {
        var payloads = new List<byte[]>();

        while (true)
        {
            var batch = await buffer.ReadBatchAsync(257, 1024 * 1024, TestContext.Current.CancellationToken);

            if (!batch.HasProgress)
            {
                return payloads;
            }

            payloads.AddRange(batch.Payloads);
            await buffer.AcknowledgeAsync(batch, TestContext.Current.CancellationToken);
        }
    }

    private sealed class BufferTestDirectory : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            "nvm-buffer-tests",
            Guid.NewGuid().ToString("N"));

        internal PersistentBufferOptions Options(long segmentBytes = 1024 * 1024, long maxBytes = 16 * 1024 * 1024) =>
            new()
            {
                DirectoryPath = _path,
                SegmentBytes = segmentBytes,
                MaxBytes = maxBytes,
                MaxRecordBytes = (int)Math.Min(4096, segmentBytes - FileStoreAndForwardBuffer.RecordOverhead),
            };

        internal string[] Segments() =>
            Directory.GetFiles(_path, "segment-*.nvmq").Order(StringComparer.Ordinal).ToArray();

        internal string SingleSegment() => Segments().ShouldHaveSingleItem();

        internal string Cursor() => Path.Combine(_path, "cursor.nvmc");

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
