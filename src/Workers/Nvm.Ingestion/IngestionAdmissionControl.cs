using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Nvm.Ingestion;

/// <summary>Biến "ingestion đang quá tải" thành một câu trả lời mà gateway có thể hành động theo.</summary>
/// <remarks>
/// <para>
/// Lựa chọn thay thế là chấp nhận mọi thứ và để PostgreSQL quyết định. Điều đó thất bại dưới dạng
/// pool timeout rải rác khắp mọi caller — mỗi gateway thấy một lỗi chậm, không rõ lý do, không cái
/// nào được bảo là hãy chậm lại, và những cái retry mạnh tay nhất sẽ thắng. Từ chối sớm với một
/// status và một delay là hình dạng thất bại duy nhất mà một caller có thể phản ứng đúng.
/// </para>
/// <para>
/// <c>429</c> thay vì <c>503</c>: service vẫn khỏe mạnh và request vẫn ổn — chỉ là có quá nhiều
/// request cùng lúc. <c>503</c> được giữ riêng cho trường hợp ingestion thực sự không phục vụ được,
/// đúng như những gì gateway đã thấy khi container bị down.
/// </para>
/// </remarks>
public static class IngestionAdmissionControl
{
    /// <summary>Tên của policy gắn vào batch endpoint.</summary>
    public const string PolicyName = "ingestion-batches";

    /// <summary>Đăng ký concurrency limit biến saturation thành một 429 kèm Retry-After.</summary>
    /// <param name="services">Service collection của ingestion host.</param>
    /// <param name="options">Cấu hình ingestion đã được validate.</param>
    public static void AddIngestionAdmissionControl(this IServiceCollection services, IngestionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(options.RetryAfter.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddConcurrencyLimiter(PolicyName, concurrency =>
            {
                concurrency.PermitLimit = options.MaxConcurrentBatches;
                concurrency.QueueLimit = options.MaxQueuedBatches;
                // FIFO trên nhiều gateway. Mỗi gateway giữ một batch đang chờ, nên queue là một hàng
                // các khu vực nhà máy khác nhau; phục vụ cái mới nhất trước sẽ để một khu vực bận
                // rộn làm đói một khu vực yên tĩnh mà buffer của nó cũng bền bỉ và đầy y hệt.
                concurrency.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });

            limiter.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds;
                return ValueTask.CompletedTask;
            };
        });
    }
}
