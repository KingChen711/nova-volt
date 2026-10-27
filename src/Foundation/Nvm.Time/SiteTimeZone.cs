namespace Nvm.Time;

/// <summary>Resolve các IANA zone id mà factory model mang theo.</summary>
/// <remarks>
/// <para>
/// Một wrapper trên một lời gọi framework, và nó tồn tại vì thông điệp báo lỗi. <c>Asia/Ho_Chi_Minh</c>
/// và <c>Europe/Berlin</c> chỉ resolve được vì <c>ADR-020</c> đã từ chối <c>InvariantGlobalization</c>:
/// bật cờ đó lên, .NET hoàn toàn không mang zone database nào cả và
/// <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> ném lỗi cho mọi IANA id có tồn tại.
/// </para>
/// <para>
/// Cờ này đúng là kiểu thứ ai đó sẽ bật lên sau này để giảm kích thước container image, và lỗi nó gây
/// ra là một <c>TimeZoneNotFoundException</c> sâu bên trong một phép tính shift mà không có gì trong
/// đó gợi ý tới một build setting. Nên thông điệp nói rõ điều này ở đây, một lần, nơi nó thực sự sẽ
/// được đọc.
/// </para>
/// </remarks>
public static class SiteTimeZone
{
    /// <summary>Resolve một zone theo IANA id của nó.</summary>
    /// <param name="ianaId">Ví dụ <c>Europe/Berlin</c>.</param>
    /// <exception cref="TimeZoneNotFoundException">
    /// Runtime không có zone như vậy — thường vì globalization data đã bị bỏ ra khỏi build.
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
