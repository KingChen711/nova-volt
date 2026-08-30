namespace Nvm.Time;

/// <summary>Thrown when the production calendar is asked about a plant it has no calendar for.</summary>
/// <remarks>
/// Its own type rather than an <see cref="InvalidOperationException"/> with a message, because the
/// caller that can do something sensible about it — a host still loading its factory model, an
/// operator screen naming the plant on the page — has to be able to tell it from every other reason
/// the calendar could fail.
/// </remarks>
public sealed class UnknownSiteException : InvalidOperationException
{
    /// <summary>Names the plant that has no calendar.</summary>
    /// <param name="siteId">The plant code that was asked about.</param>
    public UnknownSiteException(string siteId)
        : base($"No production calendar for site '{siteId}'. The calendar refuses to guess a time zone: "
            + "a wrong one files a whole shift under the wrong production day and nothing goes red.") =>
        SiteId = siteId;

    /// <summary>Creates the exception with a message of the caller's own.</summary>
    public UnknownSiteException(string siteId, string message)
        : base(message) => SiteId = siteId;

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public UnknownSiteException(string siteId, string message, Exception innerException)
        : base(message, innerException) => SiteId = siteId;

    /// <summary>The plant that was asked about.</summary>
    public string SiteId { get; }
}
