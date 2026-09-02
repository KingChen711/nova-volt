using System.Globalization;
using System.Net;

namespace Nvm.EdgeGateway.Forwarding;

/// <summary>Ingestion từ chối một batch bằng cách yêu cầu gateway chậm lại, chứ không phải bằng cách fail.</summary>
/// <remarks>
/// Kế thừa từ <see cref="HttpRequestException"/> để nhánh "did not forward" hiện có của flusher
/// vẫn chỉ là một nhánh duy nhất. Điều subtype này bổ sung là sự khác biệt mà flusher phải hành
/// động theo: một connection refused không nói gì về nhịp độ, trong khi <c>429</c>/<c>503</c> cùng
/// <c>Retry-After</c> là ingestion đang gọi tên nhịp độ nó có thể chịu đựng được.
/// </remarks>
public sealed class GatewayBackpressureException : HttpRequestException
{
    /// <summary>Tạo lời từ chối có kiểu (typed refusal) mang theo gợi ý pacing riêng của server.</summary>
    /// <param name="statusCode">Status mà ingestion trả lời.</param>
    /// <param name="retryAfter">Delay mà ingestion yêu cầu, khi nó có nêu tên một giá trị.</param>
    public GatewayBackpressureException(HttpStatusCode statusCode, TimeSpan? retryAfter)
        : base(Describe(statusCode, retryAfter), inner: null, statusCode)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>Delay mà ingestion yêu cầu. Null khi response không mang header nào dùng được.</summary>
    public TimeSpan? RetryAfter { get; }

    private static string Describe(HttpStatusCode statusCode, TimeSpan? retryAfter) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Ingestion answered {0} and asked the gateway to slow down (Retry-After: {1}).",
            (int)statusCode,
            retryAfter is null ? "absent" : retryAfter.Value.ToString("c", CultureInfo.InvariantCulture));
}
