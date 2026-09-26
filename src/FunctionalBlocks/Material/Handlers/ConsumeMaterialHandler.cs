using System.Collections.Immutable;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Ports;
using Nvm.Contracts.Queries;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Material.Commands;
using Nvm.Material.Entities;

namespace Nvm.Material.Handlers;

/// <summary>
/// Ghi tiêu hao vào stream của unit tiêu thụ (<c>consumption:{serial}</c>), không vào stream của lot:
/// một lot vào hàng chục nghìn cell, nếu lot là stream thì mọi cell tranh chấp một stream (ADR-044).
/// </summary>
public sealed class ConsumeMaterialHandler(IEventStore events, IUnitExecutionContextReader units, IMaterialLotStore lots,
    IMaterialHoldCheck holds, TimeProvider clock)
    : ICommandHandler<ConsumeMaterialCommand, DomainCommandResult>
{
    public const string StreamType = "unit-consumption";

    public static string StreamId(string serialNumber) => "consumption:" + serialNumber;

    public async Task<DomainCommandResult> HandleAsync(ConsumeMaterialCommand command, CancellationToken cancellationToken)
    {
        if (!ValidSpan(command))
        { return DomainCommandResult.Reject(MaterialReasonCodes.InvalidSpan, "Khoảng mét trên cuộn không hợp lệ."); }
        var unit = await units.ReadForCommandAsync(command.ConsumerSerialNumber, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        { return DomainCommandResult.Reject(MaterialReasonCodes.UnitNotFound, "Không tìm thấy unit tiêu thụ."); }
        if (unit.QualityState == "Held")
        { return DomainCommandResult.Reject(MaterialReasonCodes.QualityHold, "Unit đang bị giữ chất lượng."); }
        if (unit.QualityState == "Scrapped")
        { return DomainCommandResult.Reject(MaterialReasonCodes.Scrapped, "Unit đã bị loại bỏ."); }

        // Lot hoặc đoạn cuộn đang bị Quality giữ thì không được đi vào sản phẩm.
        if (await holds.ActiveHoldAsync(command.SiteId, command.LotKind, command.LotId, command.SpanFromMeter,
                command.SpanToMeter, cancellationToken).ConfigureAwait(false) is { } hold)
        { return DomainCommandResult.Reject(MaterialReasonCodes.LotOnHold, $"Vật liệu đang bị giữ chất lượng ({hold})."); }
        var now = clock.GetUtcNow();
        if (command.LotKind == "Lot")
        {
            var lot = await lots.LoadForUpdateAsync(command.SiteId, command.LotId, cancellationToken).ConfigureAwait(false);
            if (lot is null)
            { return DomainCommandResult.Reject(MaterialLotProcessor.LotNotFound, "Lot chưa được nhận vào kho."); }
            var older = await lots.OlderAvailableAsync(command.SiteId, lot, now, cancellationToken).ConfigureAwait(false);
            var overrides = await lots.OverridesAsync(command.SiteId, lot.LotId, cancellationToken).ConfigureAwait(false);
            if (MaterialRules.Check(lot, command.Quantity, command.UnitOfMeasure, now, older, overrides) is { } violation)
            { return DomainCommandResult.Reject(violation.Rule, violation.Text); }
            await lots.UpdateAsync(command.SiteId, lot with { Remaining = lot.Remaining - command.Quantity }, now,
                cancellationToken).ConfigureAwait(false);
        }
        var stream = await events.ReadStreamAsync(command.SiteId, StreamId(command.ConsumerSerialNumber),
            cancellationToken).ConfigureAwait(false);
        var fact = new MaterialLotConsumed(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.LotId, command.LotKind, command.MaterialCode, command.ConsumerSerialNumber, command.Quantity,
            command.UnitOfMeasure, command.SpanFromMeter, command.SpanToMeter, command.OperationRunId,
            command.ActorId);
        var version = await events.AppendAsync(command.SiteId, StreamId(command.ConsumerSerialNumber), StreamType,
            stream?.Version ?? 0, ImmutableArray.Create(DomainEventRecord.Create(fact,
                $"urn:material-lot:{command.LotId}", $"{command.SiteId}:{command.ConsumerSerialNumber}", now)),
            cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    /// <summary>Cuộn phải có khoảng [from, to) dương; lot thường không được mang khoảng mét.</summary>
    public static bool ValidSpan(ConsumeMaterialCommand command) => command.LotKind == "Roll"
        ? command.SpanFromMeter is { } from && command.SpanToMeter is { } to && from >= 0 && to > from
        : command.SpanFromMeter is null && command.SpanToMeter is null;
}
