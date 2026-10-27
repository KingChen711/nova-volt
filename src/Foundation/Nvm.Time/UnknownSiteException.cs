namespace Nvm.Time;

/// <summary>Ném ra khi production calendar bị hỏi về một plant mà nó không có calendar cho.</summary>
/// <remarks>
/// Một type riêng thay vì một <see cref="InvalidOperationException"/> kèm message, vì caller có thể
/// làm điều gì đó hợp lý với nó — một host đang nạp factory model, một màn hình operator nêu tên
/// plant trên trang — phải có khả năng phân biệt nó với mọi lý do khác khiến calendar có thể fail.
/// </remarks>
public sealed class UnknownSiteException : InvalidOperationException
{
    /// <summary>Nêu tên plant không có calendar.</summary>
    /// <param name="siteId">Plant code đã được hỏi tới.</param>
    public UnknownSiteException(string siteId)
        : base($"No production calendar for site '{siteId}'. The calendar refuses to guess a time zone: "
            + "a wrong one files a whole shift under the wrong production day and nothing goes red.") =>
        SiteId = siteId;

    /// <summary>Tạo exception với một message riêng của caller.</summary>
    public UnknownSiteException(string siteId, string message)
        : base(message) => SiteId = siteId;

    /// <summary>Tạo exception với một message và một inner cause.</summary>
    public UnknownSiteException(string siteId, string message, Exception innerException)
        : base(message, innerException) => SiteId = siteId;

    /// <summary>Plant đã được hỏi tới.</summary>
    public string SiteId { get; }
}
