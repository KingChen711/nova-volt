using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Nvm.Ingestion;

/// <summary>Turns "ingestion is saturated" into an answer the gateway can act on.</summary>
/// <remarks>
/// <para>
/// The alternative is to accept everything and let PostgreSQL decide. That fails as pool timeouts
/// scattered across all callers — every gateway sees a slow, unexplained error, none of them is
/// told to slow down, and the ones that retry hardest win. Refusing early with a status and a
/// delay is the only shape of failure a caller can respond to correctly.
/// </para>
/// <para>
/// <c>429</c> rather than <c>503</c>: the service is healthy and the request is fine — there are
/// just too many of them at once. <c>503</c> stays reserved for ingestion actually being unable to
/// serve, which is what the gateway already sees when the container is down.
/// </para>
/// </remarks>
public static class IngestionAdmissionControl
{
    /// <summary>Name of the policy attached to the batch endpoint.</summary>
    public const string PolicyName = "ingestion-batches";

    /// <summary>Registers the concurrency limit that turns saturation into a 429 plus Retry-After.</summary>
    /// <param name="services">Service collection of the ingestion host.</param>
    /// <param name="options">Validated ingestion configuration.</param>
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
                // FIFO across gateways. Each gateway keeps one batch outstanding, so the queue is a
                // line of distinct plant areas; serving the newest first would let a busy area
                // starve a quiet one whose buffer is just as durable and just as full.
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
