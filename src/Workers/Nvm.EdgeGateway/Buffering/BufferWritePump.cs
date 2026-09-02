using System.Threading.Channels;
using Nvm.Sparkplug;

namespace Nvm.EdgeGateway.Buffering;

/// <summary>Gom các lượt gửi MQTT chạy đồng thời lại sau một lần fsync file duy nhất.</summary>
public sealed partial class BufferWritePump : BackgroundService, IGatewayBufferWriter
{
    private readonly FileStoreAndForwardBuffer _buffer;
    private readonly PersistentBufferOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<BufferWritePump> _logger;
    private readonly Channel<WriteRequest> _requests;

    /// <summary>Tạo một handoff có giới hạn (bounded) để RAM không thể trở thành một buffer không giới hạn thứ hai.</summary>
    public BufferWritePump(
        FileStoreAndForwardBuffer buffer,
        PersistentBufferOptions options,
        TimeProvider clock,
        ILogger<BufferWritePump> logger)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _buffer = buffer;
        _options = options;
        _clock = clock;
        _logger = logger;
        _requests = Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(options.PendingWriteCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <inheritdoc />
    public async ValueTask<Task> QueueAsync(
        IReadOnlyCollection<DecodedSparkplugMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one decoded message is required.", nameof(messages));
        }

        var payloads = messages
            .Select(message => SparkplugIngressBatchCodec.Encode([message]))
            .ToArray();
        var request = new WriteRequest(payloads);

        await _requests.Writer.WriteAsync(request, cancellationToken);
        return request.Completion.Task;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(stoppingToken))
            {
                var requests = await CollectBatchAsync(stoppingToken);
                await PersistAsync(requests, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host đang shutdown. Callback MQTT nào còn đang chờ sẽ nhận cancellation qua chính token của nó.
        }
        finally
        {
            _requests.Writer.TryComplete();

            while (_requests.Reader.TryRead(out var abandoned))
            {
                abandoned.Completion.TrySetCanceled(stoppingToken);
            }
        }
    }

    private async Task<List<WriteRequest>> CollectBatchAsync(CancellationToken cancellationToken)
    {
        var requests = new List<WriteRequest>();
        var records = 0;

        if (_requests.Reader.TryRead(out var first))
        {
            requests.Add(first);
            records += first.Payloads.Length;
        }

        using var deadline = new CancellationTokenSource(_options.FsyncInterval, _clock);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        while (records < _options.FsyncBatchSize)
        {
            while (records < _options.FsyncBatchSize && _requests.Reader.TryRead(out var next))
            {
                requests.Add(next);
                records += next.Payloads.Length;
            }

            if (records >= _options.FsyncBatchSize)
            {
                break;
            }

            try
            {
                if (!await _requests.Reader.WaitToReadAsync(combined.Token))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }
        }

        return requests;
    }

    private async Task PersistAsync(List<WriteRequest> requests, CancellationToken cancellationToken)
    {
        var payloads = requests
            .SelectMany(request => request.Payloads)
            .Select(payload => (ReadOnlyMemory<byte>)payload)
            .ToArray();

        try
        {
            await _buffer.AppendBatchAsync(payloads, cancellationToken);

            foreach (var request in requests)
            {
                request.Completion.TrySetResult();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PersistFailed(_logger, exception, payloads.Length);

            foreach (var request in requests)
            {
                request.Completion.TrySetException(exception);
            }
        }
    }

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Error,
        Message = "Failed to fsync {RecordCount} gateway buffer records")]
    private static partial void PersistFailed(ILogger logger, Exception exception, int recordCount);

    private sealed record WriteRequest(byte[][] Payloads)
    {
        internal TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
