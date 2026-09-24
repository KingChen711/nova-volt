using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nvm.Projections;

/// <summary>Processes only durable inbox rows; never scans SQL history on each poll.</summary>
public sealed class ProductionUnitProjectionWorker : BackgroundService
{
    private readonly ProductionUnitProjectionInbox _inbox;
    private readonly string[] _sites;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProductionUnitProjectionWorker> _logger;

    public ProductionUnitProjectionWorker(ProductionUnitProjectionInbox inbox, IEnumerable<string> sites,
        TimeProvider clock, ILogger<ProductionUnitProjectionWorker> logger)
    {
        _inbox = inbox;
        _sites = sites.Distinct(StringComparer.Ordinal).ToArray();
        if (_sites.Length == 0)
        { throw new ArgumentException("At least one projection site is required.", nameof(sites)); }
        foreach (var site in _sites)
        { ProjectionIdentity.ValidateSite(site); }
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            var failed = false;
            foreach (var site in _sites)
            {
                try
                {
                    processed += await _inbox.DispatchAsync(site, cancellationToken: stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                { return; }
                catch (Exception error)
                {
                    failed = true;
                    _logger.LogError(error, "Unit projection failed for {SiteId}; inbox retained for retry", site);
                }
            }
            if (processed == 0 || failed)
            {
                await Task.Delay(failed ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(250),
                    _clock, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
