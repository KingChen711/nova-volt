using System.Collections.Immutable;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.ProductionExecution.Commands;

namespace Nvm.ProductionExecution.Handlers;

/// <summary>Một cuộn chỉ được phủ một lần; bản đồ đoạn là bất biến sau khi ghi.</summary>
public sealed class RecordRollCoatedHandler(IEventStore events, TimeProvider clock)
    : ICommandHandler<RecordRollCoatedCommand, DomainCommandResult>
{
    public const string StreamType = "electrode-roll";

    public static string StreamId(string rollId) => "roll:" + rollId;

    public async Task<DomainCommandResult> HandleAsync(RecordRollCoatedCommand command, CancellationToken cancellationToken)
    {
        if (RecordRollCoatedCommand.Overlaps(command.Segments))
        { return DomainCommandResult.Reject(RollReasonCodes.OverlappingSegments, "Hai đoạn cùng mặt bị chồng lấn."); }
        var stream = await events.ReadStreamAsync(command.SiteId, StreamId(command.RollId), cancellationToken)
            .ConfigureAwait(false);
        if (stream is not null && stream.Version > 0)
        { return DomainCommandResult.Reject(RollReasonCodes.AlreadyCoated, "Cuộn này đã được ghi nhận phủ."); }
        var now = clock.GetUtcNow();
        var fact = new RollCoated(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.RollId, command.Segments, command.ActorId);
        var version = await events.AppendAsync(command.SiteId, StreamId(command.RollId), StreamType, 0,
            ImmutableArray.Create(DomainEventRecord.Create(fact, $"urn:electrode-roll:{command.RollId}",
                $"{command.SiteId}:{command.RollId}", now)), cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }
}
