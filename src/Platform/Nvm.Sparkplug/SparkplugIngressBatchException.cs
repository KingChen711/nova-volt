namespace Nvm.Sparkplug;

/// <summary>The gateway-to-ingestion protobuf body is malformed or internally inconsistent.</summary>
public sealed class SparkplugIngressBatchException : Exception
{
    /// <summary>Creates a contract error with a reason safe to put in a rejected-message log.</summary>
    public SparkplugIngressBatchException(string message)
        : base(message)
    {
    }

    /// <summary>Wraps the protobuf parser while keeping it out of the public API.</summary>
    public SparkplugIngressBatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
