using System.Globalization;
using System.Text;

namespace Nvm.Foundation.Runtime;

/// <summary>Môi trường không có client gateway: gửi đồng bộ trả lỗi, gửi bất đồng bộ ném lỗi (bên gửi chỉ ghi trace).</summary>
public sealed class MissingMessageGateway : IMessageGateway
{
    public MessageReply Send(OutboundMessage message, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(message);
        return MessageReply.Failed($"No client gateway: messages/synchronous?type={message.MessageType}&adapter={message.Adapter} is not reachable.");
    }

    public void Post(OutboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        throw new InvalidOperationException($"No client gateway: messages/asynchronous?type={message.MessageType}&adapter={message.Adapter} is not reachable.");
    }
}

/// <summary>Một adapter của tầng tích hợp: nơi tin của một tên adapter đi tới.</summary>
public interface IMessageAdapter
{
    MessageReply Send(OutboundMessage message, TimeSpan timeout);

    void Post(OutboundMessage message);
}

/// <summary>
/// Gateway chọn adapter theo tên khai trong cấu hình của môi trường. Adapter không khai thì như thiếu gateway: lỗi, không đoán
/// nơi nhận.
/// </summary>
public sealed class RoutingMessageGateway : IMessageGateway
{
    public IDictionary<string, IMessageAdapter> Adapters { get; } = new Dictionary<string, IMessageAdapter>(StringComparer.Ordinal);

    public MessageReply Send(OutboundMessage message, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Adapters.TryGetValue(message.Adapter, out var adapter)
            ? adapter.Send(message, timeout)
            : MessageReply.Failed($"Adapter {message.Adapter} is not configured.");
    }

    public void Post(OutboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Adapters.TryGetValue(message.Adapter, out var adapter))
        {
            throw new InvalidOperationException($"Adapter {message.Adapter} is not configured.");
        }

        adapter.Post(message);
    }
}

/// <summary>
/// Adapter ghi tin ra file trong một thư mục, tên <c>&lt;MessageType&gt;_&lt;giờ UTC&gt;.&lt;đuôi&gt;</c>: phần mềm in nhãn, hệ đọc file
/// lấy file ở đó. Gửi đồng bộ trả lời rỗng sau khi ghi xong.
/// </summary>
public sealed class FileMessageAdapter(string directory, TimeProvider time) : IMessageAdapter
{
    public MessageReply Send(OutboundMessage message, TimeSpan timeout)
    {
        Post(message);
        return MessageReply.Of(string.Empty);
    }

    public void Post(OutboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Directory.CreateDirectory(directory);
        var extension = message.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? "xml"
            : message.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase) ? "json" : "txt";
        var stamp = time.GetUtcNow().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{message.MessageType}_{stamp}.{extension}");
        for (var n = 1; File.Exists(path); n++)
        {
            path = Path.Combine(directory, $"{message.MessageType}_{stamp}_{n}.{extension}");
        }

        File.WriteAllText(path, message.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

/// <summary>
/// Adapter gọi một client gateway qua HTTP: <c>POST &lt;base&gt;/messages/synchronous?type=…&amp;adapter=…</c> (chờ trả lời) hoặc
/// <c>/messages/asynchronous</c>. Trả lời rỗng của lời gọi đồng bộ là lỗi, như code của team kiểm.
/// </summary>
public sealed class HttpMessageAdapter(Uri gateway, HttpClient client) : IMessageAdapter
{
    public MessageReply Send(OutboundMessage message, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            using var response = client.Send(Request(message, "synchronous"), cancel.Token);
            using var reader = new StreamReader(response.Content.ReadAsStream(cancel.Token));
            var body = reader.ReadToEnd();
            if (!response.IsSuccessStatusCode)
            {
                return MessageReply.Failed($"{(int)response.StatusCode} from {message.Adapter}: {body}");
            }

            return string.IsNullOrWhiteSpace(body) ? MessageReply.Failed("The reply is empty.") : MessageReply.Of(body);
        }
        catch (OperationCanceledException)
        {
            return MessageReply.Failed($"No reply from {message.Adapter} within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            return MessageReply.Failed($"{Endpoint(message, "synchronous")} is not reachable: {ex.Message}");
        }
    }

    public void Post(OutboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var response = client.Send(Request(message, "asynchronous"));
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage Request(OutboundMessage message, string mode) => new(HttpMethod.Post, Endpoint(message, mode))
    {
        Content = new StringContent(message.Content, Encoding.UTF8, message.ContentType),
    };

    private Uri Endpoint(OutboundMessage message, string mode) =>
        new(gateway, $"messages/{mode}?type={Uri.EscapeDataString(message.MessageType)}&adapter={Uri.EscapeDataString(message.Adapter)}");
}
