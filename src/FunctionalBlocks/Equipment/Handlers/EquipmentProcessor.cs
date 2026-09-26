using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Equipment;
using Nvm.Equipment.Commands;
using Nvm.Equipment.Entities;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;

namespace Nvm.Equipment.Handlers;

/// <summary>Máy, cây lý do, ideal cycle và sản lượng trong transaction của command.</summary>
public interface IEquipmentStore
{
    Task<EquipmentState?> LoadForUpdateAsync(string siteId, string equipmentPath, CancellationToken cancellationToken);

    Task RegisterAsync(string siteId, EquipmentState state, DateTimeOffset at, CancellationToken cancellationToken);

    Task UpdateStateAsync(string siteId, EquipmentState state, DateTimeOffset at, CancellationToken cancellationToken);

    Task<DowntimeReason?> ReasonAsync(string siteId, string reasonCode, CancellationToken cancellationToken);

    Task AddDowntimeAsync(string siteId, EquipmentDowntimeRecorded downtime, CancellationToken cancellationToken);

    /// <summary>Version ideal cycle có hiệu lực tại <paramref name="at"/>; null nếu chưa có master data.</summary>
    Task<(int Version, int CycleMilliseconds)?> IdealCycleAtAsync(string siteId, string equipmentClass, string productCode,
        DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Version mới của ideal cycle; version tăng dần theo khoá.</summary>
    Task<int> AddIdealCycleAsync(string siteId, string equipmentClass, string productCode, int cycleMilliseconds,
        DateTimeOffset effectiveFrom, string actorId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>false khi khoảng đếm này của máy đã được ghi.</summary>
    Task<bool> AddCountAsync(string siteId, ProductionCountRecorded count, CancellationToken cancellationToken);
}

public sealed class EquipmentProcessor(IEventStore events, IEquipmentStore equipment, TimeProvider clock) :
    ICommandHandler<RegisterEquipmentCommand, DomainCommandResult>,
    ICommandHandler<ChangeEquipmentStateCommand, DomainCommandResult>,
    ICommandHandler<RecordProductionCountCommand, DomainCommandResult>,
    ICommandHandler<SetIdealCycleTimeCommand, DomainCommandResult>
{
    public const string StreamType = "equipment";

    public static string StreamId(string equipmentPath) => "equipment:" + equipmentPath;

    public async Task<DomainCommandResult> HandleAsync(RegisterEquipmentCommand command, CancellationToken cancellationToken)
    {
        if (await equipment.LoadForUpdateAsync(command.SiteId, command.EquipmentPath, cancellationToken).ConfigureAwait(false)
            is not null)
        { return DomainCommandResult.Reject(EquipmentReasonCodes.EquipmentExists, "Máy đã được đăng ký."); }
        var now = clock.GetUtcNow();
        var fact = new EquipmentStateChanged(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.EquipmentPath, null, EquipmentStates.Running, null, command.ActorId);
        var version = await AppendAsync(command.SiteId, command.EquipmentPath, 0, [fact], now, cancellationToken)
            .ConfigureAwait(false);
        await equipment.RegisterAsync(command.SiteId, new EquipmentState(command.EquipmentPath, command.EquipmentClass,
            EquipmentStates.Running, command.OccurredAt, null, version), now, cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(ChangeEquipmentStateCommand command, CancellationToken cancellationToken)
    {
        var current = await equipment.LoadForUpdateAsync(command.SiteId, command.EquipmentPath, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        { return DomainCommandResult.Reject(EquipmentReasonCodes.EquipmentNotFound, "Không tìm thấy máy."); }
        if (current.State == command.State)
        { return DomainCommandResult.Reject(EquipmentReasonCodes.AlreadyInState, $"Máy đang {current.State}."); }
        if (command.OccurredAt < current.StateSince)
        { return DomainCommandResult.Reject(EquipmentReasonCodes.OutOfOrder, "Thời điểm đổi trạng thái trước trạng thái hiện tại."); }
        var now = clock.GetUtcNow();
        List<IDomainEvent> facts = [];
        if (command.State == EquipmentStates.Stopped)
        {
            var reason = await equipment.ReasonAsync(command.SiteId, command.ReasonCode!, cancellationToken).ConfigureAwait(false);
            if (reason is null)
            { return DomainCommandResult.Reject(EquipmentReasonCodes.UnknownReason, "Mã lý do dừng không có trong cây lý do."); }
            if (!reason.IsLeaf)
            { return DomainCommandResult.Reject(EquipmentReasonCodes.ReasonNotLeaf, "Chọn lý do chi tiết (lá), không chọn nhóm."); }
        }
        else
        {
            // Chạy lại: đóng lần dừng đang mở và phân loại theo độ dài thật của nó.
            var reason = await equipment.ReasonAsync(command.SiteId, current.ReasonCode!, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Lý do {current.ReasonCode} của lần dừng đang mở đã mất khỏi cây lý do.");
            facts.Add(new EquipmentDowntimeRecorded(
                DeterministicGuid.CreateVersion5(command.IdempotencyKey.Value, "downtime"), command.OccurredAt, now,
                command.SiteId, command.EquipmentPath, current.StateSince, command.OccurredAt, reason.Code,
                DowntimeRules.Classify(reason.Category, command.OccurredAt - current.StateSince), command.ActorId));
        }
        var changed = new EquipmentStateChanged(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.EquipmentPath, current.State, command.State, command.ReasonCode, command.ActorId);
        facts.Insert(0, changed);
        var version = await AppendAsync(command.SiteId, command.EquipmentPath, current.StreamVersion, facts, now,
            cancellationToken).ConfigureAwait(false);
        foreach (var downtime in facts.OfType<EquipmentDowntimeRecorded>())
        { await equipment.AddDowntimeAsync(command.SiteId, downtime, cancellationToken).ConfigureAwait(false); }
        await equipment.UpdateStateAsync(command.SiteId, current with
        {
            State = command.State,
            StateSince = command.OccurredAt,
            ReasonCode = command.ReasonCode,
            StreamVersion = version
        }, now, cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(changed.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(RecordProductionCountCommand command, CancellationToken cancellationToken)
    {
        var current = await equipment.LoadForUpdateAsync(command.SiteId, command.EquipmentPath, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        { return DomainCommandResult.Reject(EquipmentReasonCodes.EquipmentNotFound, "Không tìm thấy máy."); }
        if (await equipment.IdealCycleAtAsync(command.SiteId, current.EquipmentClass, command.ProductCode, command.WindowFrom,
                cancellationToken).ConfigureAwait(false) is not { } ideal)
        {
            return DomainCommandResult.Reject(EquipmentReasonCodes.NoIdealCycle,
                $"Chưa có ideal cycle time cho {current.EquipmentClass}/{command.ProductCode}.");
        }
        var now = clock.GetUtcNow();
        var fact = new ProductionCountRecorded(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.EquipmentPath, command.ProductCode, command.WindowFrom, command.WindowTo, command.TotalCount,
            command.GoodCount, ideal.CycleMilliseconds, ideal.Version, command.ActorId);
        if (!await equipment.AddCountAsync(command.SiteId, fact, cancellationToken).ConfigureAwait(false))
        { return DomainCommandResult.Reject(EquipmentReasonCodes.CountWindowExists, "Khoảng đếm này đã được ghi."); }
        var version = await AppendAsync(command.SiteId, command.EquipmentPath, current.StreamVersion, [fact], now,
            cancellationToken).ConfigureAwait(false);
        await equipment.UpdateStateAsync(command.SiteId, current with { StreamVersion = version }, now, cancellationToken)
            .ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(SetIdealCycleTimeCommand command, CancellationToken cancellationToken)
    {
        var version = await equipment.AddIdealCycleAsync(command.SiteId, command.EquipmentClass, command.ProductCode,
            command.CycleMilliseconds, command.EffectiveFrom, command.ActorId, clock.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, null, null,
            $"{command.EquipmentClass}/{command.ProductCode} v{version}");
    }

    private Task<long> AppendAsync(string siteId, string equipmentPath, long expected, IReadOnlyList<IDomainEvent> facts,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        events.AppendAsync(siteId, StreamId(equipmentPath), StreamType, expected, [.. facts.Select(fact =>
            DomainEventRecord.Create(fact, "urn:equipment:" + equipmentPath, $"{siteId}:equipment:{equipmentPath}", now))],
            cancellationToken);
}
