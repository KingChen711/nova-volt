using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Child-process modes used by <c>make buffer-crash</c>.</summary>
/// <remarks>
/// The writer is intentionally killed from outside. No shutdown hook, finally block or Dispose path
/// participates in the proof. The verifier then opens the same files through production recovery.
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

        // Seed and acknowledge a prefix before the crash window. Every reopen therefore exercises
        // a durable, non-zero cursor as well as the append tail; a cursor beyond valid data makes
        // the verifier fail while reading through the production recovery path.
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

        // The host waits for ReadyFile and then creates StartFile. This removes container startup
        // time from the random kill offset; without the handshake a fast writer can finish every
        // round while `docker exec test -f` is still starting.
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

            // Keep a real crash window open across many fsync boundaries. Without this probe-only
            // pause, a fast machine can finish all 500 records before Docker delivers SIGKILL.
            await Task.Delay(1 + ((round + next) % 4));
        }

        // The harness must deliver SIGKILL even when all records happened to finish quickly.
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

    // There is no asynchronous API for fsync. FlushAsync above empties managed buffers; this call
    // is the physical durability boundary whose completion is written to confirmed.txt.
    private static void DurableFlush(FileStream stream) => stream.Flush(flushToDisk: true);
}
