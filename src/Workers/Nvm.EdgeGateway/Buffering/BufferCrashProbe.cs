using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Các mode child-process dùng bởi <c>make buffer-crash</c>.</summary>
/// <remarks>
/// Writer bị kill từ bên ngoài một cách cố ý. Không shutdown hook, finally block hay Dispose path
/// nào được tham gia vào phép chứng minh này. Verifier sau đó mở lại đúng những file đó qua
/// đường phục hồi (recovery) của production.
/// </remarks>
internal static class BufferCrashProbe
{
    private const string WriterMode = "--buffer-crash-writer";
    private const string VerifyMode = "--buffer-crash-verify";
    private const string ConfirmationFile = "confirmed.txt";
    private const string ReadyFile = "writer-ready";
    private const string StartFile = "writer-start";

    internal static bool IsRequested(string[] args) =>
        args.Contains(WriterMode, StringComparer.Ordinal)
        || args.Contains(VerifyMode, StringComparer.Ordinal);

    internal static async Task<int> RunAsync(string[] args, PersistentBufferOptions options)
    {
        var directory = Required(args, "--directory");
        var round = int.Parse(Required(args, "--round"), NumberStyles.None, CultureInfo.InvariantCulture);
        options.DirectoryPath = directory;
        options.MaxBytes = 64L * 1024 * 1024;
        options.SegmentBytes = 1024 * 1024;
        options.MaxRecordBytes = 4096;
        options.Validate();

        return args.Contains(WriterMode, StringComparer.Ordinal)
            ? await WriteUntilKilledAsync(options, round)
            : await VerifyAsync(options, round);
    }

    private static async Task<int> WriteUntilKilledAsync(PersistentBufferOptions options, int round)
    {
        Directory.CreateDirectory(options.DirectoryPath);
        await using var buffer = new FileStoreAndForwardBuffer(options);

        // Seed và acknowledge một đoạn prefix trước cửa sổ crash. Nhờ vậy mỗi lần reopen đều
        // luyện tới cả cursor bền (durable, khác 0) lẫn phần đuôi mới append; cursor vượt quá
        // dữ liệu hợp lệ sẽ khiến verifier fail khi đọc qua đường phục hồi của production.
        var preludeCount = 1 + ((round * 29) % 17);
        await buffer.AppendBatchAsync(
            Enumerable
                .Range(10_000, preludeCount)
                .Select(index => (ReadOnlyMemory<byte>)Payload(round, index))
                .ToArray());
        var prelude = await buffer.ReadBatchAsync(preludeCount, 4 * 1024 * 1024);
        await buffer.AcknowledgeAsync(prelude);

        var confirmationPath = Path.Combine(options.DirectoryPath, ConfirmationFile);
        await using var confirmations = new FileStream(
            confirmationPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

        await using (var ready = new FileStream(
            Path.Combine(options.DirectoryPath, ReadyFile),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await ready.WriteAsync(new byte[] { 1 });
            await ready.FlushAsync();
            DurableFlush(ready);
        }

        // Host chờ ReadyFile rồi mới tạo StartFile. Việc này loại thời gian khởi động container
        // ra khỏi offset kill ngẫu nhiên; không có bắt tay (handshake) này thì một writer chạy
        // nhanh có thể xong hết mọi round trong khi `docker exec test -f` vẫn còn đang khởi động.
        var startPath = Path.Combine(options.DirectoryPath, StartFile);

        while (!File.Exists(startPath))
        {
            await Task.Delay(1);
        }

        var count = 1 + ((round * 73) % 500);
        var next = 0;

        while (next < count)
        {
            var batchSize = Math.Min(1 + ((round + next) % 19), count - next);
            var payloads = Enumerable
                .Range(next, batchSize)
                .Select(index => (ReadOnlyMemory<byte>)Payload(round, index))
                .ToArray();

            await buffer.AppendBatchAsync(payloads);

            foreach (var payload in payloads)
            {
                var line = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(payload.Span)) + "\n");
                await confirmations.WriteAsync(line);
            }

            await confirmations.FlushAsync();
            DurableFlush(confirmations);
            next += batchSize;

            // Giữ cửa sổ crash thật mở xuyên qua nhiều ranh giới fsync. Không có khoảng dừng
            // chỉ-dùng-cho-probe này, một máy chạy nhanh có thể xong cả 500 record trước khi
            // Docker kịp gửi SIGKILL.
            await Task.Delay(1 + ((round + next) % 4));
        }

        // Harness phải gửi được SIGKILL kể cả khi mọi record tình cờ xong hết rất nhanh.
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> VerifyAsync(PersistentBufferOptions options, int round)
    {
        await using var buffer = new FileStoreAndForwardBuffer(options);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var batch = await buffer.ReadBatchAsync(512, 4 * 1024 * 1024);

            if (!batch.HasProgress)
            {
                break;
            }

            foreach (var payload in batch.Payloads)
            {
                if (!TryValidatePayload(payload, round))
                {
                    await Console.Error.WriteLineAsync("A recovered record has the wrong round, index or digest.");
                    return 21;
                }

                seen.Add(Convert.ToHexString(SHA256.HashData(payload)));
            }

            await buffer.AcknowledgeAsync(batch);
        }

        var confirmationPath = Path.Combine(options.DirectoryPath, ConfirmationFile);
        var confirmed = File.Exists(confirmationPath)
            ? await File.ReadAllLinesAsync(confirmationPath)
            : [];

        foreach (var digest in confirmed.Where(line => line.Length == SHA256.HashSizeInBytes * 2))
        {
            if (!seen.Contains(digest))
            {
                await Console.Error.WriteLineAsync(
                    $"Confirmed fsync record {digest} is missing after SIGKILL recovery.");
                return 22;
            }
        }

        var snapshot = buffer.Snapshot;

        if (snapshot.CorruptRecords != 0)
        {
            await Console.Error.WriteLineAsync(
                $"Recovery returned {snapshot.CorruptRecords} CRC-corrupt records.");
            return 23;
        }

        await Console.Out.WriteLineAsync(
            FormattableString.Invariant(
                $"round={round} confirmed={confirmed.Length} recovered={seen.Count} truncated={snapshot.TruncatedTails}"));
        return 0;
    }

    private static byte[] Payload(int round, int index)
    {
        var body = Encoding.ASCII.GetBytes(
            FormattableString.Invariant($"round={round};index={index};padding={new string('x', index % 257)}"));
        var digest = SHA256.HashData(body);
        var payload = new byte[body.Length + digest.Length];
        body.CopyTo(payload, 0);
        digest.CopyTo(payload, body.Length);
        return payload;
    }

    private static bool TryValidatePayload(byte[] payload, int expectedRound)
    {
        if (payload.Length < SHA256.HashSizeInBytes)
        {
            return false;
        }

        var body = payload.AsSpan(0, payload.Length - SHA256.HashSizeInBytes);
        var digest = payload.AsSpan(body.Length);

        return body.StartsWith(Encoding.ASCII.GetBytes(FormattableString.Invariant($"round={expectedRound};")))
            && SHA256.HashData(body).AsSpan().SequenceEqual(digest);
    }

    private static string Required(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : throw new ArgumentException($"Crash probe requires {name} <value>.", nameof(args));
    }

    // Không có API bất đồng bộ nào cho fsync. FlushAsync ở trên chỉ làm rỗng managed buffer;
    // lệnh gọi này mới là ranh giới bền vật lý (physical durability), và việc nó hoàn tất mới
    // được ghi vào confirmed.txt.
    private static void DurableFlush(FileStream stream) => stream.Flush(flushToDisk: true);
}
