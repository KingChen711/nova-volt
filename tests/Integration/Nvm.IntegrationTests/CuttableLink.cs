using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Nvm.IntegrationTests;

/// <summary>A TCP forwarder whose sockets can be destroyed without a protocol goodbye.</summary>
/// <remarks>
/// <para>
/// The only way to produce what a cut cable produces: no DISCONNECT packet. Asking MQTTnet to
/// disconnect would be a clean goodbye, and a clean goodbye tells the broker to DISCARD the will —
/// the exact case these tests exist to rule out.
/// </para>
/// <para>
/// Shared by every test that has to kill a live MQTT connection rather than close one. Two copies of
/// this would drift, and the half that drifted would be the half whose test still passed.
/// </para>
/// </remarks>
internal sealed class CuttableLink : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ConcurrentBag<TcpClient> _live = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _targetHost;
    private readonly int _targetPort;

    /// <summary>Starts forwarding a loopback port at the given target.</summary>
    /// <param name="targetHost">Where the real broker is.</param>
    /// <param name="targetPort">The real broker's port.</param>
    public CuttableLink(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    /// <summary>The loopback port a client connects to instead of the broker.</summary>
    public int Port { get; }

    /// <summary>Destroys every live socket, in both directions, without a goodbye.</summary>
    public void Cut()
    {
        while (_live.TryTake(out var client))
        {
            try
            {
                // Linger 0 sends RST rather than FIN, so neither side gets an orderly shutdown.
                client.LingerState = new LingerOption(true, 0);
                client.Close();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Already gone is the outcome we wanted.
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
            // A cut connection lands here by design; the tests assert on what the broker does about
            // it, not on this forwarder's tidiness.
        }
    }
}
