using System.Collections.Immutable;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Traceability;
using Nvm.Contracts.Queries;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Entities;
using Nvm.Traceability.Ports;

namespace Nvm.Traceability.Handlers;

/// <summary>
/// Lắp, tháo và sửa quan hệ cha–con. Chỉ khoá stream membership của unit con; cha chỉ được kiểm
/// tồn tại và chất lượng, nên nhiều con lắp vào cùng một cha chạy song song (ADR-044).
/// </summary>
public sealed class GenealogyCommandProcessor(
    IEventStore events, IUnitRegistry units, IUnitQualityFacet quality, TimeProvider clock)
{
    public async Task<UnitCommandResult> AssembleAsync(AssembleUnitCommand command, CancellationToken ct)
    {
        var (membership, reason) = await LoadAsync(command, command.ParentSerialNumber, ct).ConfigureAwait(false);
        if (reason is not null)
        { return Reject(reason); }
        if (membership!.ParentSerialNumber is not null)
        { return Reject(GenealogyReasonCodes.AlreadyAssembled); }
        var now = clock.GetUtcNow();
        var fact = new UnitAssembledInto(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.SerialNumber, command.ParentSerialNumber, command.Position, command.OperationRunId,
            command.ActorId);
        return await AppendAsync(command, membership, fact, now, ct).ConfigureAwait(false);
    }

    public async Task<UnitCommandResult> RemoveAsync(RemoveUnitCommand command, CancellationToken ct)
    {
        var (membership, reason) = await LoadAsync(command, command.ParentSerialNumber, ct, checkQuality: false)
            .ConfigureAwait(false);
        if (reason is not null)
        { return Reject(reason); }
        if (membership!.ParentSerialNumber is null)
        { return Reject(GenealogyReasonCodes.NotAssembled); }
        if (membership.ParentSerialNumber != command.ParentSerialNumber)
        { return Reject(GenealogyReasonCodes.ParentMismatch); }
        var now = clock.GetUtcNow();
        var fact = new UnitRemovedFrom(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.SerialNumber, command.ParentSerialNumber, command.ReasonCode, command.OperationRunId,
            command.ActorId);
        return await AppendAsync(command, membership, fact, now, ct).ConfigureAwait(false);
    }

    public async Task<UnitCommandResult> CorrectAsync(CorrectGenealogyCommand command, CancellationToken ct)
    {
        // Sửa sai không phải thao tác vật lý: unit đang bị giữ vẫn được sửa hồ sơ.
        var (membership, reason) = await LoadAsync(command, command.CorrectParentSerialNumber, ct,
            checkQuality: false).ConfigureAwait(false);
        if (reason is not null)
        { return Reject(reason); }
        if (membership!.ParentSerialNumber is null)
        { return Reject(GenealogyReasonCodes.NotAssembled); }
        if (membership.ParentSerialNumber != command.WrongParentSerialNumber)
        { return Reject(GenealogyReasonCodes.ParentMismatch); }
        var now = clock.GetUtcNow();
        var fact = new GenealogyCorrectionRecorded(command.IdempotencyKey.Value, command.OccurredAt, now,
            command.SiteId, command.SerialNumber, command.WrongParentSerialNumber,
            command.CorrectParentSerialNumber, command.Position, command.ReasonText, command.ActorId);
        return await AppendAsync(command, membership, fact, now, ct).ConfigureAwait(false);
    }

    private async Task<(UnitMembership? Membership, string? Reason)> LoadAsync(UnitCommand command,
        string parentSerial, CancellationToken ct, bool checkQuality = true)
    {
        if (!SerialNumber.TryParse(command.SerialNumber, out var child) ||
            !SerialNumber.TryParse(parentSerial, out var parent))
        { return (null, UnitReasonCodes.InvalidSerial); }
        if (child.SiteCode != command.SiteId || parent.SiteCode != command.SiteId)
        { return (null, UnitReasonCodes.SiteMismatch); }
        if (!UnitMembership.CanContain(parent.Kind, child.Kind))
        { return (null, GenealogyReasonCodes.InvalidHierarchy); }
        if (!await units.ExistsAsync(command.SiteId, command.SerialNumber, ct).ConfigureAwait(false))
        { return (null, UnitReasonCodes.UnitNotFound); }
        if (!await units.ExistsAsync(command.SiteId, parentSerial, ct).ConfigureAwait(false))
        { return (null, GenealogyReasonCodes.ParentNotFound); }
        if (checkQuality)
        {
            // Unit đang bị giữ không được tiêu dùng vào cha; cha đã loại bỏ không nhận thêm con.
            var childQuality = await quality.ReadForCommandAsync(command.SiteId, command.SerialNumber, ct)
                .ConfigureAwait(false);
            if (childQuality.BlockingReasonCode is { } blocked)
            { return (null, blocked); }
            var parentQuality = await quality.ReadForCommandAsync(command.SiteId, parentSerial, ct)
                .ConfigureAwait(false);
            if (parentQuality.QualityState == "Scrapped")
            { return (null, GenealogyReasonCodes.ParentNotUsable); }
        }
        var stream = await events.ReadStreamAsync(command.SiteId, UnitMembership.StreamId(command.SerialNumber), ct)
            .ConfigureAwait(false);
        return (UnitMembership.Replay(command.SiteId, command.SerialNumber, stream), null);
    }

    private async Task<UnitCommandResult> AppendAsync(UnitCommand command, UnitMembership membership,
        IDomainEvent fact, DateTimeOffset now, CancellationToken ct)
    {
        var kind = SerialNumber.Parse(command.SerialNumber).Kind.ToString().ToLowerInvariant();
        var version = await events.AppendAsync(command.SiteId, UnitMembership.StreamId(command.SerialNumber),
            UnitMembership.StreamType, membership.Version,
            ImmutableArray.Create(DomainEventRecord.Create(fact, $"urn:trace-unit:{kind}:{command.SerialNumber}",
                $"{command.SiteId}:{command.SerialNumber}", now)), ct).ConfigureAwait(false);
        return new UnitCommandResult(true, UnitReasonCodes.Accepted, fact.EventId, version);
    }

    private static UnitCommandResult Reject(string reason) => new(false, reason, null, null);
}

public sealed class AssembleUnitHandler(GenealogyCommandProcessor processor)
    : ICommandHandler<AssembleUnitCommand, UnitCommandResult>
{
    public Task<UnitCommandResult> HandleAsync(AssembleUnitCommand command, CancellationToken cancellationToken) =>
        processor.AssembleAsync(command, cancellationToken);
}

public sealed class RemoveUnitHandler(GenealogyCommandProcessor processor)
    : ICommandHandler<RemoveUnitCommand, UnitCommandResult>
{
    public Task<UnitCommandResult> HandleAsync(RemoveUnitCommand command, CancellationToken cancellationToken) =>
        processor.RemoveAsync(command, cancellationToken);
}

public sealed class CorrectGenealogyHandler(GenealogyCommandProcessor processor)
    : ICommandHandler<CorrectGenealogyCommand, UnitCommandResult>
{
    public Task<UnitCommandResult> HandleAsync(CorrectGenealogyCommand command, CancellationToken cancellationToken) =>
        processor.CorrectAsync(command, cancellationToken);
}
