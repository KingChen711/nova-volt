namespace Nvm.EdgeGateway;

/// <summary>Một topic Sparkplug hợp lệ về cú pháp nhưng không đặt tên cho bất kỳ equipment nào trong plant model đang active.</summary>
public sealed class UnknownEquipmentTopicException : Exception
{
    /// <summary>Tạo một lời từ chối nêu tên topic gây ra lỗi.</summary>
    public UnknownEquipmentTopicException(string topic)
        : base($"Sparkplug topic '{topic}' names no equipment in the active factory model.")
    {
        Topic = topic;
    }

    /// <summary>MQTT topic đã bị từ chối (K3).</summary>
    public string Topic { get; }
}
