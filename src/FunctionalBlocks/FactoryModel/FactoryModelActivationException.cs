namespace Nvm.FactoryModel;

/// <summary>Thrown when a factory model revision cannot be put in force.</summary>
/// <remarks>
/// A business refusal, not a malformed request. The command was well formed and the answer is still
/// no — because the plant is not in the document, because the document has moved on since the caller
/// read it, or because the revision would take the plant backwards. Kept apart from validation so a
/// caller can tell "fix your request" from "the world is not in the state you assumed".
/// </remarks>
public sealed class FactoryModelActivationException : Exception
{
    /// <summary>Creates the exception with a message describing the refusal.</summary>
    public FactoryModelActivationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying failure.</summary>
    public FactoryModelActivationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no message.</summary>
    public FactoryModelActivationException()
        : base("The factory model revision could not be activated.")
    {
    }
}
