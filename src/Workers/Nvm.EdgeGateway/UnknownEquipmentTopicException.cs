namespace Nvm.EdgeGateway;

/// <summary>A syntactically valid Sparkplug topic names no equipment in the active plant model.</summary>
public sealed class UnknownEquipmentTopicException : Exception
{
    /// <summary>Creates a rejection that names the offending topic.</summary>
    public UnknownEquipmentTopicException(string topic)
        : base($"Sparkplug topic '{topic}' names no equipment in the active factory model.")
    {
        Topic = topic;
    }

    /// <summary>The MQTT topic that was refused (K3).</summary>
    public string Topic { get; }
}
