using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.BusProbe.Consumers;

/// <summary>Stands in for the service that keeps a cached copy of the plant tree.</summary>
/// <param name="logger">Where the one line of evidence per message goes.</param>
/// <remarks>
/// <para>
/// The real one arrives in M2: any service that resolves an equipment path holds the tree in memory,
/// and a revision it never heard about is a cache that answers confidently and wrongly. That is why
/// this is the consumer worth imitating — the fan-out being demonstrated is not a demonstration.
/// </para>
/// <para>
/// Its own queue, not a share of one. Two consumers on one queue compete and each message reaches
/// exactly one of them; two consumers on two queues both receive every message. The difference is
/// invisible in code and decides whether the audit trail alongside it is complete or missing half its
/// entries.
/// </para>
/// </remarks>
[BusEndpoint("factory-model", "cache-updater")]
public sealed partial class FactoryModelCacheProbe(ILogger<FactoryModelCacheProbe> logger)
    : IConsumer<FactoryModelRevisionActivated>
{
    private readonly ILogger<FactoryModelCacheProbe> _logger = logger;

    /// <inheritdoc />
    public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (eventId, _, siteId, revision, nodeCount, added, removed) = context.Message;

        Received(revision, siteId, nodeCount, added.Count, removed.Count, eventId);

        return Task.CompletedTask;
    }

    // Source-generated rather than a plain _logger.LogInformation(...). CA1873, new in .NET 10,
    // refuses an Information-level call carrying more than one property: the arguments are boxed into
    // an array before anything asks whether the level is switched on. The generator emits the
    // IsEnabled check first, so nothing is paid for a line that is not written.
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "cache-updater received revision {Revision} at {SiteId}: {NodeCount} nodes, "
            + "{Added} added, {Removed} removed (ce_id {EventId})")]
    private partial void Received(
        int revision,
        string siteId,
        int nodeCount,
        int added,
        int removed,
        Guid eventId);
}
