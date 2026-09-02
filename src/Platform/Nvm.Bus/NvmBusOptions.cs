namespace Nvm.Bus;

/// <summary>Cách để kết nối tới broker.</summary>
/// <remarks>
/// Chỉ là các thiết lập kết nối. Mọi thứ về <i>hình dạng</i> — tên exchange, routing key, tên queue —
/// nằm trong <see cref="Topology.NvmTopology"/> và không thể cấu hình được, vì một topology khác nhau
/// giữa các môi trường là một topology mà không ai luận ra được nữa.
/// </remarks>
public sealed class NvmBusOptions
{
    /// <summary>Tên host của broker. Ở development, là container được publish trên localhost.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Cổng AMQP.</summary>
    public ushort Port { get; set; } = 5672;

    /// <summary>Virtual host. Mỗi môi trường một virtual host sẽ là một cải tiến hợp lý về sau.</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>Người dùng broker.</summary>
    /// <remarks>
    /// Không bao giờ là <c>guest</c>. RabbitMQ chỉ chấp nhận <c>guest</c> từ loopback của chính broker,
    /// nên một service tưởng như hoạt động được với nó ở một môi trường triển khai sẽ fail ở môi trường
    /// kế tiếp với một lỗi authentication trông giống như sự cố firewall.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>Mật khẩu broker.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Deployable nào mà tiến trình này đại diện, viết kebab-case — <c>app-execution</c>, <c>ingestion</c>.
    /// </summary>
    /// <remarks>
    /// Nửa sau của mọi CloudEvents source URN mà tiến trình này publish:
    /// <c>urn:novavolt:nv1:app-execution</c>. Source trả lời câu "ai nói vậy", và đây là thứ đầu tiên
    /// ai đó nhìn vào khi hai service bất đồng về cùng một unit — nên nó phải nêu tên một deployable,
    /// không phải một máy hay một class.
    /// </remarks>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>Ném lỗi khi các option không mô tả được một broker có thể kết nối tới.</summary>
    /// <exception cref="InvalidOperationException">Thiếu một giá trị bắt buộc.</exception>
    public void Validate()
    {
        // Fail ngay lúc container đang được xây dựng, không phải ở lần publish đầu tiên. Một mật khẩu
        // bị thiếu sẽ lộ ra lúc khởi động thành một dòng rõ ràng, hoặc hai giờ sau thành một consumer
        // chưa từng nhận được gì và một broker log không ai theo dõi.
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException("Bus host is required.");
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            throw new InvalidOperationException(
                "Bus credentials are required. In development they come from .env "
                + "(NVM_RABBITMQ_USER, NVM_RABBITMQ_PASSWORD) — the same file docker-compose reads.");
        }

        if (string.IsNullOrWhiteSpace(ApplicationName))
        {
            throw new InvalidOperationException(
                "Bus application name is required: it becomes the CloudEvents source of every event "
                + "this process publishes.");
        }
    }
}
