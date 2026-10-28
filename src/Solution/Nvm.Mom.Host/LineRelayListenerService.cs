using System.Text.Json;
using Nvm.Foundation.Runtime;
using Nvm.Project;
using Nvm.Shopfloor.LineDb;

namespace Nvm.Mom.Host;

/// <summary>
/// Bên nghe của đường relay (vai bộ tích hợp của nhà cung cấp nền): mỗi notify của LineDB thành một event
/// <see cref="LineMessageArrived"/> trong MOM. Mất kết nối thì nghe lại sau vài giây; notify gửi trong lúc đó mất, dòng vẫn
/// SENT và chờ chính sách gửi lại.
/// </summary>
internal sealed partial class LineRelayListenerService(string lineDb, CommandExecutor executor, ILogger<LineRelayListenerService> logger)
    : BackgroundService
{
    /// <summary>Tên event trên model: <c>&lt;FB&gt;.&lt;Event&gt;</c>.</summary>
    private const string EventName = "FB_OP_Nv." + nameof(LineMessageArrived);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var listener = new LineNotifyListener(lineDb, payload =>
                {
                    var body = JsonSerializer.Serialize(new LineMessageArrived("relay", payload));
                    var correlationId = executor.RaiseExternalEvent(EventName, body);
                    Received(logger, payload.Length, correlationId);
                    return Task.CompletedTask;
                });
                await listener.StartAsync(stoppingToken).ConfigureAwait(false);
                Listening(logger, LineRelay.Channel);
                await listener.RunAsync(stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // LineDB chưa lên hoặc rớt kết nối: nghe lại nhịp sau
            catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
            {
                ListenFailed(logger, e);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for line messages on channel {Channel}")]
    private static partial void Listening(ILogger logger, string channel);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Line message notify ({Bytes} bytes) raised as event, correlation {CorrelationId}")]
    private static partial void Received(ILogger logger, int bytes, Guid correlationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Line message listener stopped; retrying")]
    private static partial void ListenFailed(ILogger logger, Exception exception);
}
