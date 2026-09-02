using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Nvm.IntegrationTests;

/// <summary>Một bộ chuyển tiếp TCP mà các socket của nó có thể bị phá hủy mà không có lời tạm biệt giao thức.</summary>
/// <remarks>
/// <para>
/// Cách duy nhất để tạo ra đúng thứ mà một cáp bị cắt tạo ra: không có gói DISCONNECT. Yêu cầu
/// MQTTnet ngắt kết nối sẽ là một lời tạm biệt sạch sẽ, và một lời tạm biệt sạch sẽ báo cho broker
/// DISCARD will — chính là trường hợp mà các test này tồn tại để loại trừ.
/// </para>
/// <para>
/// Được dùng chung bởi mọi test cần giết một kết nối MQTT đang sống thay vì đóng nó. Hai bản sao của
/// thứ này sẽ lệch nhau theo thời gian, và nửa bị lệch sẽ là nửa mà test của nó vẫn pass.
/// </para>
/// </remarks>
internal sealed class CuttableLink : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentBag<TcpClient> _live = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _targetHost;
    private readonly int _targetPort;

    /// <summary>Bắt đầu chuyển tiếp một loopback port tới target đã cho.</summary>
    /// <param name="targetHost">Nơi broker thật đang chạy.</param>
    /// <param name="targetPort">Port của broker thật.</param>
    public CuttableLink(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    /// <summary>Loopback port mà client kết nối tới thay vì broker.</summary>
    public int Port { get; }

    /// <summary>Phá hủy mọi socket đang sống, theo cả hai chiều, mà không có lời tạm biệt.</summary>
    public void Cut()
    {
        while (_live.TryTake(out var client))
        {
            try
            {
                // Linger 0 gửi RST thay vì FIN, nên không bên nào nhận được một shutdown có trật tự.
                client.LingerState = new LingerOption(true, 0);
                client.Close();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Đã biến mất chính là kết quả ta muốn.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        Cut();
        _listener.Stop();
        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient inbound;

            try
            {
                inbound = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException
                or ObjectDisposedException)
            {
                return;
            }

            _live.Add(inbound);
            _ = ForwardBothWaysAsync(inbound);
        }
    }

    private async Task ForwardBothWaysAsync(TcpClient inbound)
    {
        try
        {
            var outbound = new TcpClient();
            await outbound.ConnectAsync(_targetHost, _targetPort, _stopping.Token);
            _live.Add(outbound);

            var upstream = inbound.GetStream().CopyToAsync(outbound.GetStream(), _stopping.Token);
            var downstream = outbound.GetStream().CopyToAsync(inbound.GetStream(), _stopping.Token);

            await Task.WhenAny(upstream, downstream);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Một kết nối bị cắt rơi vào đây theo thiết kế; các test assert vào việc broker phản ứng
            // thế nào với nó, chứ không phải vào sự gọn gàng của bộ chuyển tiếp này.
        }
    }
}
