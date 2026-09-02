using System.Globalization;

namespace Nvm.LoadHarness;

/// <summary>Đẩy mạnh cỡ nào, trong bao lâu, và vào cái gì.</summary>
public sealed class LoadHarnessOptions
{
    /// <summary>Tên service EMQX trên <c>ot-net</c>.</summary>
    public string BrokerHost { get; set; } = "emqx";

    /// <summary>Cổng MQTT listener bên trong Docker network.</summary>
    public int BrokerPort { get; set; } = 1883;

    /// <summary>Line mà harness này giả vờ là.</summary>
    public string LinePath { get; set; } = "NOVAVOLT/NV1/FORMATION/F1";

    /// <summary>Directory chứa các document factory-model, để lấy danh sách channel.</summary>
    public string SeedDirectory { get; set; } = "seed";

    /// <summary>Target publish rate, tính bằng message mỗi giây. N1 là 5.000.</summary>
    public int Rate { get; set; } = 5_000;

    /// <summary>Duy trì trong bao lâu. D2 yêu cầu mười phút.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Số publish QoS 1 tối đa đang chờ acknowledgement trên MQTT session của node.</summary>
    /// <remarks>
    /// <para>
    /// Concurrency là một in-flight window, không phải thêm MQTT client. Sparkplug gán một session có
    /// thứ tự cho một edge node, nên mở một client cho mỗi worker sẽ tạo ra nhiều sequence stream
    /// không tương thích dưới cùng một node topic.
    /// </para>
    /// <para>
    /// Window này còn là trần thông lượng của chính publisher trong lúc nó là ràng buộc quyết định:
    /// ở QoS 1 không có nhiều hơn số này publish đang treo, nên rate không thể vượt quá window chia
    /// cho độ trễ acknowledgement. Một window 32 đã giữ nguồn ở mức 4.933 msg/s và khiến D2 fail vì
    /// công cụ đo chứ không phải vì pipeline.
    /// </para>
    /// <para>
    /// Ở đây nó không còn là ràng buộc nữa. Đo ngày 2026-08-29: 32 cho ra 4.933 msg/s, 128 cho ra
    /// 5.119 và 256 cho ra 5.104 - phẳng lì, vì một broker acknowledge một publisher ngay khi nó chấp
    /// nhận message và không bao giờ chờ subscriber, nên mở rộng window quá rate của bên nhận chẳng
    /// mua thêm được gì. 256 được giữ lại để có headroom chống stall, không phải để tăng throughput.
    /// </para>
    /// </remarks>
    public int MaxInFlightPublishes { get; set; } = 256;

    /// <summary>Xây options từ biến môi trường, để container không cần tham số nào.</summary>
    public static LoadHarnessOptions FromEnvironment()
    {
        var options = new LoadHarnessOptions();

        Set("NVM_LOAD_BROKER_HOST", value => options.BrokerHost = value);
        Set("NVM_LOAD_LINE_PATH", value => options.LinePath = value);
        Set("NVM_LOAD_SEED_DIRECTORY", value => options.SeedDirectory = value);
        Set("NVM_LOAD_BROKER_PORT", value => options.BrokerPort = ParseInt(value));
        Set("NVM_LOAD_RATE", value => options.Rate = ParseInt(value));
        Set("NVM_LOAD_MAX_IN_FLIGHT", value => options.MaxInFlightPublishes = ParseInt(value));
        Set("NVM_LOAD_DURATION_SECONDS", value => options.Duration = TimeSpan.FromSeconds(ParseInt(value)));

        options.Validate();
        return options;
    }

    /// <summary>Từ chối các setting không thể tạo ra một phép đo.</summary>
    /// <exception cref="InvalidOperationException">Một giá trị nằm ngoài phạm vi.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(BrokerHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(LinePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BrokerPort);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Rate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInFlightPublishes);

        if (Duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The run duration must be positive.");
        }
    }

    private static void Set(string name, Action<string> assign)
    {
        var value = Environment.GetEnvironmentVariable(name);

        if (!string.IsNullOrWhiteSpace(value))
        {
            assign(value.Trim());
        }
    }

    private static int ParseInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);
}
