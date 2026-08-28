namespace Nvm.Simulator.Publishing;

/// <summary>A message could not be put on the wire.</summary>
/// <remarks>
/// <para>
/// The link failed, not the plant. The distinction is the whole reason this type exists: the worker
/// has to keep running when the broker goes away (N15) and must NOT keep running when the line
/// itself is wrong, and a bare <c>catch</c> around the publish would make those two indistinguishable.
/// </para>
/// <para>
/// Declared here rather than letting MQTTnet's own exceptions travel, because the worker is written
/// against <see cref="ISparkplugPublisher"/> and has no business knowing which transport is under it.
/// A publisher that spoke a different protocol would report the same failure the same way.
/// </para>
/// </remarks>
public sealed class SparkplugPublishException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SparkplugPublishException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What could not be sent, and where it was going.</param>
    public SparkplugPublishException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What could not be sent, and where it was going.</param>
    /// <param name="innerException">What the transport said.</param>
    public SparkplugPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
