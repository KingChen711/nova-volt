namespace Nvm.Sparkplug;

/// <summary>Thrown when a Sparkplug payload cannot be read as a set of device readings.</summary>
/// <remarks>
/// <para>
/// Every path in the decoder either produces a reading or throws. There is no arm that returns fewer
/// metrics than arrived, because the caller has no way to tell that apart from report-by-exception
/// deciding nothing changed — the whole point of RBE is that a short payload is normal.
/// </para>
/// <para>
/// The same lesson the bus already learned twice in M1: <i>unreadable</i> is not <i>absent</i>. There
/// it was a CloudEvents header read through <c>as string</c>; here it is a metric quietly skipped. A
/// consumer that drops what it does not understand reports success on data it never saw.
/// </para>
/// <para>
/// Recoverable at the message level, not at the process level. Ingestion answers this by moving the
/// payload aside with its reason attached — the file-drop path calls that <c>rejected/</c> and the bus
/// calls it <c>_error</c> — and carries on with the next message. A malformed payload from one channel
/// must not stop the other 999.
/// </para>
/// </remarks>
public class SparkplugDecodeException : Exception
{
    /// <summary>Creates the exception with a message describing what is wrong with the payload.</summary>
    public SparkplugDecodeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying failure.</summary>
    public SparkplugDecodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no message.</summary>
    public SparkplugDecodeException()
        : base("The Sparkplug payload could not be decoded.")
    {
    }
}
