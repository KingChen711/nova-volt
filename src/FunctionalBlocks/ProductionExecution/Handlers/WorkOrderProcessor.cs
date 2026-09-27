using System.Collections.Immutable;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Contracts.Ports;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;
using Nvm.ProductionExecution.Commands;

namespace Nvm.ProductionExecution.Handlers;

/// <summary>Work order đã nhận, kèm dữ liệu ERP gốc để đánh giá lại khi master data đổi.</summary>
public sealed record StoredWorkOrder(string WorkOrderId, string ScheduleId, string ExternalProductCode, string? ProductCode,
    DateTimeOffset? EarliestStart, ImmutableArray<WorkOrderMaterial> Materials, string Status,
    ImmutableArray<string> OpenTaskIds, long StreamVersion);

public interface IWorkOrderStore
{
    Task<StoredWorkOrder?> LoadForUpdateAsync(string siteId, string workOrderId, CancellationToken cancellationToken);

    Task AddAsync(string siteId, StoredWorkOrder workOrder, DateTimeOffset at, CancellationToken cancellationToken);

    Task UpdateAsync(string siteId, StoredWorkOrder workOrder, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>
/// Nhận work order từ ERP và phát xuống khi master data khớp. Mã lạ hoặc đơn vị lệch không làm mất lệnh và không bị
/// quy đổi: lệnh vào <c>PendingMasterData</c> kèm task đối soát (scope §7.6).
/// </summary>
public sealed class WorkOrderProcessor(IEventStore events, IWorkOrderStore workOrders, IMasterDataReconciliation masterData,
    TimeProvider clock) :
    ICommandHandler<ReceiveWorkOrderCommand, DomainCommandResult>,
    ICommandHandler<ReevaluateWorkOrderCommand, DomainCommandResult>
{
    public const string StreamType = "work-order";

    public static string StreamId(string workOrderId) => "work-order:" + workOrderId;

    public async Task<DomainCommandResult> HandleAsync(ReceiveWorkOrderCommand command, CancellationToken cancellationToken)
    {
        if (await workOrders.LoadForUpdateAsync(command.SiteId, command.WorkOrderId, cancellationToken).ConfigureAwait(false)
            is { } existing)
        {
            return DomainCommandResult.Reject(WorkOrderReasonCodes.WorkOrderExists,
                $"Work order {command.WorkOrderId} đã nhận từ lịch {existing.ScheduleId}.");
        }
        var now = clock.GetUtcNow();
        var materials = command.Materials.Select(m => new WorkOrderMaterial(m.ExternalMaterialId.Trim(), null, m.Quantity,
            m.UnitOfMeasure.Trim())).ToImmutableArray();
        var draft = new StoredWorkOrder(command.WorkOrderId, command.ScheduleId, command.ExternalProductCode,
            null, command.EarliestStart, materials, WorkOrderStatuses.PendingMasterData, [], 0);
        var evaluated = await EvaluateAsync(command.SiteId, draft, command.SourceDocument, now, cancellationToken)
            .ConfigureAwait(false);
        var received = new WorkOrderReceived(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.WorkOrderId, command.ScheduleId, command.ExternalProductCode, evaluated.ProductCode, command.EarliestStart,
            evaluated.Materials, evaluated.Status, evaluated.OpenTaskIds, command.ActorId);
        List<IDomainEvent> facts = [received];
        var revision = await masterData.RevisionAsync(command.SiteId, cancellationToken).ConfigureAwait(false);
        if (evaluated.Status == WorkOrderStatuses.Released)
        { facts.Add(Released(command, evaluated, revision, now)); }
        var version = await AppendAsync(command.SiteId, command.WorkOrderId, 0, facts, now, cancellationToken).ConfigureAwait(false);
        await workOrders.AddAsync(command.SiteId, evaluated with { StreamVersion = version }, now, cancellationToken)
            .ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, received.EventId, version, evaluated.Status);
    }

    public async Task<DomainCommandResult> HandleAsync(ReevaluateWorkOrderCommand command, CancellationToken cancellationToken)
    {
        var stored = await workOrders.LoadForUpdateAsync(command.SiteId, command.WorkOrderId, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null)
        { return DomainCommandResult.Reject(WorkOrderReasonCodes.WorkOrderNotFound, "Không tìm thấy work order."); }
        if (stored.Status != WorkOrderStatuses.PendingMasterData)
        { return DomainCommandResult.Reject(WorkOrderReasonCodes.NotPending, $"Work order đang {stored.Status}."); }
        var now = clock.GetUtcNow();
        var evaluated = await EvaluateAsync(command.SiteId, stored, $"B2MML:{stored.ScheduleId}/{stored.WorkOrderId}", now,
            cancellationToken).ConfigureAwait(false);
        if (evaluated.Status != WorkOrderStatuses.Released)
        {
            await workOrders.UpdateAsync(command.SiteId, evaluated, now, cancellationToken).ConfigureAwait(false);
            return DomainCommandResult.Reject(WorkOrderReasonCodes.StillPending,
                $"Còn {evaluated.OpenTaskIds.Length} task: {string.Join(", ", evaluated.OpenTaskIds)}.");
        }
        var fact = Released(command, evaluated, command.MasterDataRevision, now);
        var version = await AppendAsync(command.SiteId, command.WorkOrderId, stored.StreamVersion, [fact], now, cancellationToken)
            .ConfigureAwait(false);
        await workOrders.UpdateAsync(command.SiteId, evaluated with { StreamVersion = version }, now, cancellationToken)
            .ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    /// <summary>Giải sản phẩm và từng vật liệu; mở task cho mọi sai lệch; Released chỉ khi không còn sai lệch nào.</summary>
    private async Task<StoredWorkOrder> EvaluateAsync(string siteId, StoredWorkOrder order, string document, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        List<ReconciliationIssue> issues = [];
        var product = await masterData.ResolveAsync(siteId, IdentityKinds.Product, order.ExternalProductCode, cancellationToken)
            .ConfigureAwait(false);
        if (product is null)
        {
            issues.Add(new ReconciliationIssue(ReconciliationIssueKinds.UnknownProduct, order.ExternalProductCode, null, null, null,
                document, $"Sản phẩm '{order.ExternalProductCode}' chưa có trong master data."));
        }
        var materials = ImmutableArray.CreateBuilder<WorkOrderMaterial>(order.Materials.Length);
        foreach (var line in order.Materials)
        {
            var resolved = await masterData.ResolveAsync(siteId, IdentityKinds.Material, line.ExternalMaterialId, cancellationToken)
                .ConfigureAwait(false);
            materials.Add(line with { MaterialId = resolved?.CanonicalId });
            if (resolved is null)
            {
                issues.Add(new ReconciliationIssue(ReconciliationIssueKinds.UnknownMaterial, line.ExternalMaterialId, null,
                    line.UnitOfMeasure, null, document, $"Vật liệu '{line.ExternalMaterialId}' chưa có trong master data."));
                continue;
            }
            if (!string.Equals(resolved.BaseUom, line.UnitOfMeasure, StringComparison.Ordinal))
            {
                // Không quy đổi kg ↔ g: lệch đơn vị là dấu hiệu dữ liệu ERP sai, phải có người xác nhận.
                var mismatch = new ReconciliationIssue(ReconciliationIssueKinds.UomMismatch, line.ExternalMaterialId,
                    resolved.CanonicalId, line.UnitOfMeasure, resolved.BaseUom, document,
                    $"ERP gửi {line.Quantity} {line.UnitOfMeasure} cho {resolved.CanonicalId}, đơn vị gốc là {resolved.BaseUom}.");
                if (!await masterData.IsAcceptedAsync(siteId, mismatch, cancellationToken).ConfigureAwait(false))
                { issues.Add(mismatch); }
            }
        }
        List<string> taskIds = [];
        foreach (var issue in issues)
        { taskIds.Add(await masterData.OpenTaskAsync(siteId, issue, now, cancellationToken).ConfigureAwait(false)); }
        return order with
        {
            ProductCode = product?.CanonicalId,
            Materials = materials.MoveToImmutable(),
            Status = issues.Count == 0 ? WorkOrderStatuses.Released : WorkOrderStatuses.PendingMasterData,
            OpenTaskIds = [.. taskIds.Distinct(StringComparer.Ordinal)],
        };
    }

    private static WorkOrderReleased Released(DurableCommand command, StoredWorkOrder order, long revision, DateTimeOffset now) =>
        new(DeterministicGuid.CreateVersion5(command.IdempotencyKey.Value, "released"), command.OccurredAt, now, command.SiteId,
            order.WorkOrderId, order.ProductCode!, order.Materials, revision, command.ActorId);

    private Task<long> AppendAsync(string siteId, string workOrderId, long expected, IReadOnlyList<IDomainEvent> facts,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        events.AppendAsync(siteId, StreamId(workOrderId), StreamType, expected, [.. facts.Select(fact =>
            DomainEventRecord.Create(fact, "urn:work-order:" + workOrderId, $"{siteId}:work-order:{workOrderId}", now))],
            cancellationToken);
}
