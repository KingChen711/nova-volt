namespace Nvm.FactoryModel.Seeding;

/// <summary>Thrown when the seed file does not describe a valid plant.</summary>
/// <remarks>
/// Always fatal at startup, never recoverable. A service running on a factory model it could not
/// fully read would resolve some equipment paths and silently fail others, which is worse than not
/// starting: the gaps would look like missing data rather than a bad configuration file.
/// </remarks>
public sealed class FactoryModelSeedException : Exception
{
    /// <summary>Creates the exception with a message describing what is wrong with the file.</summary>
    public FactoryModelSeedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying failure.</summary>
    public FactoryModelSeedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no message.</summary>
    public FactoryModelSeedException()
        : base("The factory model seed is not valid.")
    {
    }
}
