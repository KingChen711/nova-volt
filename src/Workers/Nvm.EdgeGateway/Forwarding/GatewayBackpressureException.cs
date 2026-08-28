using System.Globalization;
using System.Net;

namespace Nvm.EdgeGateway.Forwarding;

/// <summary>Ingestion refused a batch by asking the gateway to slow down, not by failing.</summary>
/// <remarks>
/// Derives from <see cref="HttpRequestException"/> so the flusher's existing "did not forward"
/// path stays one branch. What the subtype adds is the difference the flusher must act on: a
/// connection refused says nothing about pace, while <c>429</c>/<c>503</c> plus <c>Retry-After</c>
/// is ingestion naming the pace it can survive.
/// </remarks>
public sealed class GatewayBackpressureException : HttpRequestException
{
    /// <summary>Creates the typed refusal carrying the server's own pacing hint.</summary>
    /// <param name="statusCode">The status ingestion answered with.</param>
    /// <param name="retryAfter">Delay ingestion asked for, when it named one.</param>
    public GatewayBackpressureException(HttpStatusCode statusCode, TimeSpan? retryAfter)
        : base(Describe(statusCode, retryAfter), inner: null, statusCode)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>Delay ingestion asked for. Null when the response carried no usable header.</summary>
    public TimeSpan? RetryAfter { get; }

    private static string Describe(HttpStatusCode statusCode, TimeSpan? retryAfter) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Ingestion answered {0} and asked the gateway to slow down (Retry-After: {1}).",
            (int)statusCode,
            retryAfter is null ? "absent" : retryAfter.Value.ToString("c", CultureInfo.InvariantCulture));
}
