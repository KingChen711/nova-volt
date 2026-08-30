namespace Nvm.Time;

/// <summary>Resolves the IANA zone ids the factory model carries.</summary>
/// <remarks>
/// <para>
/// A wrapper over one framework call, and it exists for the message. <c>Asia/Ho_Chi_Minh</c> and
/// <c>Europe/Berlin</c> only resolve because <c>ADR-020</c> refused
/// <c>InvariantGlobalization</c>: with that flag on, .NET carries no zone database at all and
/// <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> throws for every IANA id there is.
/// </para>
/// <para>
/// The flag is exactly the kind of thing someone turns on later to shave a container image, and the
/// failure it causes is a <c>TimeZoneNotFoundException</c> deep in a shift calculation with nothing in
/// it to suggest a build setting. So the message says it here, once, where it will actually be read.
/// </para>
/// </remarks>
public static class SiteTimeZone
{
    /// <summary>Resolves a zone by its IANA id.</summary>
    /// <param name="ianaId">For example <c>Europe/Berlin</c>.</param>
    /// <exception cref="TimeZoneNotFoundException">
    /// The runtime has no such zone — most often because globalization data was left out of the build.
    /// </exception>
    public static TimeZoneInfo Of(string ianaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ianaId);

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (TimeZoneNotFoundException cause)
        {
            throw new TimeZoneNotFoundException(
                $"This runtime has no time zone '{ianaId}'. If every IANA id fails, the build turned "
                + "InvariantGlobalization back on, which ADR-020 refuses precisely because it takes the "
                + "production calendar with it.",
                cause);
        }
    }
}
