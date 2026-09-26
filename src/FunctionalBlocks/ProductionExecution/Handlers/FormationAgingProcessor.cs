using System.Collections.Immutable;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Contracts.Ports;
using Nvm.Contracts.Queries;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Entities;
using Nvm.ProductionExecution.Ports;

namespace Nvm.ProductionExecution.Handlers;

/// <summary>
/// Process manager formation + aging. Mỗi command: khoá trạng thái của cell, kiểm chuyển trạng thái, append
/// event vào <c>formation:{serial}</c>, lưu trạng thái và lịch timeout trong cùng transaction.
/// </summary>
public sealed class FormationAgingProcessor(IEventStore events, IFormationProcessStore store,
    IUnitExecutionContextReader units, IQualityIncidents quality, TimeProvider clock) :
    ICommandHandler<StartFormationCommand, DomainCommandResult>,
    ICommandHandler<CompleteFormationCommand, DomainCommandResult>,
    ICommandHandler<StartAgingCommand, DomainCommandResult>,
    ICommandHandler<RecordOcv2Command, DomainCommandResult>,
    ICommandHandler<FireFormationTimeoutCommand, DomainCommandResult>
{
    public const string StreamType = "formation-aging";

    public static string StreamId(string serialNumber) => "formation:" + serialNumber;

    public async Task<DomainCommandResult> HandleAsync(StartFormationCommand command, CancellationToken cancellationToken)
    {
        var unit = await units.ReadForCommandAsync(command.SerialNumber, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        { return DomainCommandResult.Reject(FormationReasonCodes.UnitNotFound, "Không tìm thấy cell."); }
        if (unit.QualityState is "Held" or "Scrapped")
        { return DomainCommandResult.Reject(FormationReasonCodes.QualityHold, "Cell đang bị giữ chất lượng."); }
        var existing = await store.LoadForUpdateAsync(command.SiteId, command.SerialNumber, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        { return DomainCommandResult.Reject(FormationReasonCodes.AlreadyInProcess, "Cell đã có quá trình formation."); }
        var now = clock.GetUtcNow();
        var process = FormationAgingProcess.Start(command.SiteId, command.SerialNumber, command.TrayId, command.Channel,
            command.EquipmentPath, command.OccurredAt);
        var fact = new FormationRunStarted(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.SerialNumber, command.TrayId, command.Channel, command.EquipmentPath, process.FormationDueAt,
            command.ActorId);
        var version = await AppendAsync(process, fact, now, cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(process with { Version = version }, isNew: true, now, cancellationToken).ConfigureAwait(false);
        await store.ScheduleAsync(command.SiteId, command.SerialNumber, FormationAgingRules.FormationTimeoutKind,
            process.FormationDueAt, cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(CompleteFormationCommand command, CancellationToken cancellationToken)
    {
        var process = await store.LoadForUpdateAsync(command.SiteId, command.SerialNumber, cancellationToken).ConfigureAwait(false);
        if (process is null)
        { return DomainCommandResult.Reject(FormationReasonCodes.ProcessNotFound, "Cell chưa vào formation."); }
        if (process.CanCompleteFormation() is { } reason)
        { return DomainCommandResult.Reject(reason, "Quá trình không ở bước formation."); }
        var now = clock.GetUtcNow();
        var fact = new FormationRunCompleted(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.SerialNumber, command.CapacityAh, command.CurveUri, command.ActorId);
        var version = await AppendAsync(process, fact, now, cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(process with
        {
            Version = version,
            State = FormationAgingState.Degassing,
            CapacityAh = command.CapacityAh
        }, isNew: false, now, cancellationToken).ConfigureAwait(false);
        await store.CompleteTimeoutAsync(command.SiteId, command.SerialNumber, FormationAgingRules.FormationTimeoutKind,
            now, cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(StartAgingCommand command, CancellationToken cancellationToken)
    {
        var process = await store.LoadForUpdateAsync(command.SiteId, command.SerialNumber, cancellationToken).ConfigureAwait(false);
        if (process is null)
        { return DomainCommandResult.Reject(FormationReasonCodes.ProcessNotFound, "Cell chưa vào formation."); }
        if (process.CanStartAging() is { } reason)
        { return DomainCommandResult.Reject(reason, "Cell chưa degas xong hoặc đã vào aging."); }
        var now = clock.GetUtcNow();
        var due = command.OccurredAt + FormationAgingRules.AgingPeriod;
        var fact = new AgingStarted(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.SerialNumber, command.Ocv1Millivolt, process.TrayId, command.RackId, command.Level, command.Channel,
            due, command.ActorId);
        var version = await AppendAsync(process, fact, now, cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(process with
        {
            Version = version,
            State = FormationAgingState.Aging,
            Ocv1Millivolt = command.Ocv1Millivolt,
            RackId = command.RackId,
            Level = command.Level,
            AgingChannel = command.Channel,
            AgingDueAt = due
        }, isNew: false, now, cancellationToken).ConfigureAwait(false);
        await store.ScheduleAsync(command.SiteId, command.SerialNumber, FormationAgingRules.AgingDueKind, due, cancellationToken)
            .ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(RecordOcv2Command command, CancellationToken cancellationToken)
    {
        var process = await store.LoadForUpdateAsync(command.SiteId, command.SerialNumber, cancellationToken).ConfigureAwait(false);
        if (process is null)
        { return DomainCommandResult.Reject(FormationReasonCodes.ProcessNotFound, "Cell chưa vào formation."); }
        if (process.CanRecordOcv2() is { } reason)
        {
            return DomainCommandResult.Reject(reason, reason == FormationReasonCodes.AgingNotElapsed
                ? "Chưa đủ thời gian aging." : "Cell không ở bước chờ đo OCV lần 2.");
        }
        var now = clock.GetUtcNow();
        var drift = FormationAgingProcess.Drift(process.Ocv1Millivolt!.Value, command.Ocv2Millivolt);
        var passed = drift <= FormationAgingRules.DriftLimitMillivolt;
        var fact = new OcvDriftEvaluated(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.SerialNumber, process.Ocv1Millivolt.Value, command.Ocv2Millivolt, drift,
            FormationAgingRules.DriftLimitMillivolt, passed, command.ActorId);
        var version = await AppendAsync(process, fact, now, cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(process with
        {
            Version = version,
            Ocv2Millivolt = command.Ocv2Millivolt,
            State = passed ? FormationAgingState.Completed : FormationAgingState.Quarantined
        }, isNew: false, now, cancellationToken).ConfigureAwait(false);
        if (!passed)
        {
            // Nghi tự phóng điện: Quality giữ cell và mở NCR trong cùng transaction với kết quả đo.
            await quality.QuarantineWithNonConformanceAsync(command.SiteId, command.SerialNumber,
                FormationReasonCodes.OcvDrift,
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"OCV drift {drift:0.###} mV > {FormationAgingRules.DriftLimitMillivolt} mV sau aging."),
                "formation-aging", fact.EventId, command.ActorId, command.OccurredAt, cancellationToken).ConfigureAwait(false);
        }
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(FireFormationTimeoutCommand command, CancellationToken cancellationToken)
    {
        var process = await store.LoadForUpdateAsync(command.SiteId, command.SerialNumber, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (process is null || !process.TimeoutApplies(command.Kind))
        {
            await store.CompleteTimeoutAsync(command.SiteId, command.SerialNumber, command.Kind, now, cancellationToken).ConfigureAwait(false);
            return DomainCommandResult.Reject(FormationReasonCodes.InvalidState, "Timeout không còn áp dụng.");
        }
        IDomainEvent fact;
        FormationAgingProcess next;
        if (command.Kind == FormationAgingRules.AgingDueKind)
        {
            fact = new AgingPeriodElapsed(command.IdempotencyKey.Value, command.DueAt, now, command.SiteId,
                command.SerialNumber, command.DueAt);
            next = process with { State = FormationAgingState.AwaitingMeasurement };
        }
        else
        {
            fact = new FormationProcessFaulted(command.IdempotencyKey.Value, command.DueAt, now, command.SiteId,
                command.SerialNumber, FormationReasonCodes.FormationTimeout, process.State.ToString());
            next = process with { State = FormationAgingState.Faulted };
        }
        var version = await AppendAsync(process, fact, now, cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(next with { Version = version }, isNew: false, now, cancellationToken).ConfigureAwait(false);
        await store.CompleteTimeoutAsync(command.SiteId, command.SerialNumber, command.Kind, now, cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    private Task<long> AppendAsync(FormationAgingProcess process, IDomainEvent fact, DateTimeOffset now,
        CancellationToken ct) =>
        events.AppendAsync(process.SiteId, StreamId(process.SerialNumber), StreamType, process.Version,
            ImmutableArray.Create(DomainEventRecord.Create(fact, "urn:trace-unit:cell:" + process.SerialNumber,
                $"{process.SiteId}:{process.SerialNumber}", now)), ct);
}
