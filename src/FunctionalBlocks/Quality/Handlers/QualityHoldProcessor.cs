using System.Collections.Immutable;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Quality;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Quality.Commands;
using Nvm.Quality.Entities;
using Nvm.Quality.Ports;

namespace Nvm.Quality.Handlers;

/// <summary>
/// Hold, cascade hạ nguồn, chữ ký điện tử và quyết định MRB (scope §6.7, §13.3). Separation of duties và số chữ ký
/// là invariant ở đây, không phải chính sách UI.
/// </summary>
public sealed class QualityHoldProcessor(IEventStore events, IHoldStore holds, ISignatureStore signatures,
    INcrStore ncrs, IUnitQualityWriter units, IDownstreamUnits downstream, TimeProvider clock) :
    ICommandHandler<PlaceHoldCommand, DomainCommandResult>,
    ICommandHandler<PlanHoldCascadeCommand, DomainCommandResult>,
    ICommandHandler<ApplyCascadeChunkCommand, DomainCommandResult>,
    ICommandHandler<SignCommand, DomainCommandResult>,
    ICommandHandler<ReleaseHoldCommand, DomainCommandResult>,
    ICommandHandler<ApplyDispositionCommand, DomainCommandResult>
{
    public const int ChunkSize = 1000;

    public async Task<DomainCommandResult> HandleAsync(PlaceHoldCommand command, CancellationToken cancellationToken)
    {
        var holdId = "HOLD-" + command.IdempotencyKey.Value.ToString("N")[..12].ToUpperInvariant();
        var now = clock.GetUtcNow();
        var fact = new QualityHoldPlaced(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId, holdId,
            command.TargetKind, command.TargetId, command.SpanFromMeter, command.SpanToMeter, command.ReasonCode,
            command.NcrId, command.ActorId);
        var version = await AppendAsync(command.SiteId, HoldStream(holdId), "quality-hold", 0, fact, now, cancellationToken)
            .ConfigureAwait(false);
        await holds.CreateAsync(command.SiteId, new StoredHold(holdId, command.TargetKind, command.TargetId,
            command.SpanFromMeter, command.SpanToMeter, command.ReasonCode, command.NcrId, command.ActorId, "Active",
            version), now, cancellationToken).ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, fact.EventId, version, holdId);
    }

    public async Task<DomainCommandResult> HandleAsync(PlanHoldCascadeCommand command, CancellationToken cancellationToken)
    {
        var hold = await holds.LoadForUpdateAsync(command.SiteId, command.HoldId, cancellationToken).ConfigureAwait(false);
        if (hold is null)
        { return DomainCommandResult.Reject(QualityReasonCodes.HoldNotFound); }
        if (hold.Status != "Active")
        { return DomainCommandResult.Reject(QualityReasonCodes.HoldNotActive); }
        var serials = hold.TargetKind == "Unit"
            ? [hold.TargetId, .. await downstream.ReadAsync(command.SiteId, "Unit", hold.TargetId, null, null, cancellationToken)
                .ConfigureAwait(false)]
            : await downstream.ReadAsync(command.SiteId, hold.TargetKind, hold.TargetId, hold.SpanFromMeter,
                hold.SpanToMeter, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var jobId = hold.HoldId;
        var before = await holds.LoadJobForUpdateAsync(command.SiteId, jobId, cancellationToken).ConfigureAwait(false);
        var total = await holds.AddTargetsAsync(command.SiteId, jobId, hold.HoldId, serials, ChunkSize, now, cancellationToken)
            .ConfigureAwait(false);
        if (before is null)
        {
            var started = new HoldCascadeStarted(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
                hold.HoldId, jobId, total, ChunkSize);
            var startedVersion = await AppendAsync(command.SiteId, CascadeStream(jobId), "hold-cascade", 0, started, now,
                cancellationToken).ConfigureAwait(false);
            await holds.SetStreamVersionAsync(command.SiteId, jobId, startedVersion, cancellationToken).ConfigureAwait(false);
        }
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, null, total,
            $"{total} unit trong phạm vi hold.");
    }

    public async Task<DomainCommandResult> HandleAsync(ApplyCascadeChunkCommand command, CancellationToken cancellationToken)
    {
        var job = await holds.LoadJobForUpdateAsync(command.SiteId, command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null || job.NextChunk != command.ChunkIndex)
        { return DomainCommandResult.Reject(QualityReasonCodes.CascadeNotReady); }
        var hold = await holds.LoadForUpdateAsync(command.SiteId, job.HoldId, cancellationToken).ConfigureAwait(false);
        if (hold?.Status != "Active")
        { return DomainCommandResult.Reject(QualityReasonCodes.HoldNotActive); }
        var serials = await holds.ReadChunkAsync(command.SiteId, command.JobId, command.ChunkIndex, job.ChunkSize,
            cancellationToken).ConfigureAwait(false);
        var chunks = (job.TotalUnits + job.ChunkSize - 1) / job.ChunkSize;
        var last = command.ChunkIndex >= chunks - 1;
        var now = clock.GetUtcNow();
        var version = job.StreamVersion;
        var chunkFact = new UnitsHeldByCascade(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            job.HoldId, command.JobId, command.ChunkIndex, [.. serials]);
        var batch = ImmutableArray.CreateBuilder<NewStreamEvent>();
        batch.Add(Record(chunkFact, command.SiteId, CascadeStream(command.JobId), now));
        if (last)
        {
            var completed = new HoldCascadeCompleted(
                IdempotencyKey.FromNaturalKey(command.SiteId, "HoldCascadeCompleted", command.JobId, chunks.ToString(System.Globalization.CultureInfo.InvariantCulture)).Value,
                command.OccurredAt, now, command.SiteId, job.HoldId, command.JobId, job.TotalUnits, chunks);
            batch.Add(Record(completed, command.SiteId, CascadeStream(command.JobId), now));
        }
        var next = await events.AppendAsync(command.SiteId, CascadeStream(command.JobId), "hold-cascade", version,
            batch.ToImmutable(), cancellationToken).ConfigureAwait(false);
        await holds.ApplyChunkAsync(command.SiteId, command.JobId, job.HoldId, command.ChunkIndex, job.ChunkSize, serials,
            last, next, now, cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(chunkFact.EventId, next);
    }

    public async Task<DomainCommandResult> HandleAsync(SignCommand command, CancellationToken cancellationToken)
    {
        string expected;
        if (command.SubjectType == "QualityHold")
        {
            var hold = await holds.LoadForUpdateAsync(command.SiteId, command.SubjectId, cancellationToken).ConfigureAwait(false);
            if (hold is null)
            { return DomainCommandResult.Reject(QualityReasonCodes.HoldNotFound); }
            expected = hold.ReleaseContent;
        }
        else if (command.SubjectType == "NonConformance")
        {
            var ncr = await ncrs.LoadNcrForUpdateAsync(command.SiteId, command.SubjectId, cancellationToken).ConfigureAwait(false);
            if (ncr is null || command.Disposition is null)
            { return DomainCommandResult.Reject(QualityReasonCodes.NcrNotFound); }
            expected = ncr.DispositionContent(command.Disposition);
        }
        else
        { expected = command.ContentSha256; }   // Recipe/Passport: FB sở hữu đối tượng đối chiếu hash khi dùng chữ ký.
        if (!string.Equals(expected, command.ContentSha256, StringComparison.Ordinal))
        { return DomainCommandResult.Reject(QualityReasonCodes.SignatureStaleContent, "Nội dung đã thay đổi sau khi bạn xem."); }

        var now = clock.GetUtcNow();
        var previous = await signatures.HeadForUpdateAsync(command.SiteId, cancellationToken).ConfigureAwait(false);
        var signatureId = "SIG-" + command.IdempotencyKey.Value.ToString("N")[..16].ToUpperInvariant();
        var hash = SignatureChain.Hash(previous, signatureId, command.SubjectType, command.SubjectId, command.ActorId,
            command.SignerRole, command.Meaning, command.ContentSha256, now);
        var record = new SignatureRecord(signatureId, command.SubjectType, command.SubjectId, command.ActorId,
            command.SignerRole, command.Meaning, command.ContentSha256, now, previous, hash);
        await signatures.AppendAsync(command.SiteId, record, cancellationToken).ConfigureAwait(false);
        var fact = new ElectronicSignatureRecorded(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            signatureId, command.SubjectType, command.SubjectId, command.ActorId, command.SignerRole, command.Meaning,
            command.ContentSha256, previous, hash);
        var version = await AppendAsync(command.SiteId, "signature:" + signatureId, "electronic-signature", 0, fact, now,
            cancellationToken).ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, fact.EventId, version, signatureId);
    }

    public async Task<DomainCommandResult> HandleAsync(ReleaseHoldCommand command, CancellationToken cancellationToken)
    {
        var hold = await holds.LoadForUpdateAsync(command.SiteId, command.HoldId, cancellationToken).ConfigureAwait(false);
        if (hold is null)
        { return DomainCommandResult.Reject(QualityReasonCodes.HoldNotFound, "Không tìm thấy hold."); }
        if (hold.Status != "Active")
        { return DomainCommandResult.Reject(QualityReasonCodes.HoldNotActive, "Hold đã được thả."); }
        if (string.Equals(hold.HeldBy, command.ActorId, StringComparison.Ordinal))
        { return DomainCommandResult.Reject(QualityReasonCodes.SeparationOfDuties, "Người giữ không được tự thả hold."); }
        var signed = await signatures.LoadAsync(command.SiteId, command.SignatureIds, cancellationToken).ConfigureAwait(false);
        if (signed.Count != command.SignatureIds.Distinct(StringComparer.Ordinal).Count())
        { return DomainCommandResult.Reject(QualityReasonCodes.SignatureNotFound, "Có chữ ký không tồn tại."); }
        if (ApprovalPolicy.Check(signed, ApprovalPolicy.HoldRelease, hold.HeldBy, "QualityHold", hold.HoldId,
                hold.ReleaseContent) is { } reason)
        { return DomainCommandResult.Reject(reason, Describe(reason, ApprovalPolicy.HoldRelease)); }
        var now = clock.GetUtcNow();
        var fact = new QualityHoldReleased(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            hold.HoldId, [.. signed.Select(s => s.SignatureId)], command.ActorId);
        var version = await AppendAsync(command.SiteId, HoldStream(hold.HoldId), "quality-hold", hold.StreamVersion, fact,
            now, cancellationToken).ConfigureAwait(false);
        await holds.SetReleasedAsync(command.SiteId, hold.HoldId, version, now, cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(ApplyDispositionCommand command, CancellationToken cancellationToken)
    {
        var ncr = await ncrs.LoadNcrForUpdateAsync(command.SiteId, command.NcrId, cancellationToken).ConfigureAwait(false);
        if (ncr is null)
        { return DomainCommandResult.Reject(QualityReasonCodes.NcrNotFound, "Không tìm thấy NCR."); }
        if (ncr.Status == "Closed")
        { return DomainCommandResult.Reject(QualityReasonCodes.NcrClosed, "NCR đã đóng."); }
        var signed = await signatures.LoadAsync(command.SiteId, command.SignatureIds, cancellationToken).ConfigureAwait(false);
        if (signed.Count != command.SignatureIds.Distinct(StringComparer.Ordinal).Count())
        { return DomainCommandResult.Reject(QualityReasonCodes.SignatureNotFound, "Có chữ ký không tồn tại."); }
        var required = ApprovalPolicy.ForDisposition(command.Disposition);
        if (ApprovalPolicy.Check(signed, required, ncr.RaisedBy, "NonConformance", ncr.NcrId,
                ncr.DispositionContent(command.Disposition)) is { } reason)
        { return DomainCommandResult.Reject(reason, Describe(reason, required)); }
        var now = clock.GetUtcNow();
        var fact = new DispositionApplied(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId, ncr.NcrId,
            ncr.SerialNumber, command.Disposition, [.. signed.Select(s => s.SignatureId)], command.ActorId);
        var version = await AppendAsync(command.SiteId, "ncr:" + ncr.NcrId, "non-conformance", ncr.StreamVersion, fact, now,
            cancellationToken).ConfigureAwait(false);
        await ncrs.CloseAsync(command.SiteId, ncr.NcrId, command.Disposition, version, now, cancellationToken).ConfigureAwait(false);
        var state = command.Disposition switch
        {
            "Scrap" => QualityState.Scrapped,
            "Rework" => QualityState.Rework,
            _ => QualityState.Released
        };
        await units.SetStateAsync(command.SiteId, ncr.SerialNumber, state, "MRB_" + command.Disposition.ToUpperInvariant(),
            fact.EventId, command.ActorId, command.OccurredAt, cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public static string HoldStream(string holdId) => "hold:" + holdId;

    public static string CascadeStream(string jobId) => "cascade:" + jobId;

    private static string Describe(string reason, IReadOnlyCollection<string> roles) => reason switch
    {
        QualityReasonCodes.MissingSignature => "Thiếu chữ ký của: " + string.Join(", ", roles) + ".",
        QualityReasonCodes.SeparationOfDuties => "Người khởi tạo không được ký duyệt chính yêu cầu của mình.",
        QualityReasonCodes.DuplicateSigner => "Mỗi người chỉ được ký một vai trò.",
        QualityReasonCodes.SignatureStaleContent => "Chữ ký không khớp nội dung hiện tại.",
        _ => "Chữ ký không hợp lệ."
    };

    private Task<long> AppendAsync(string siteId, string stream, string type, long expected, IDomainEvent fact,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        events.AppendAsync(siteId, stream, type, expected, ImmutableArray.Create(Record(fact, siteId, stream, now)),
            cancellationToken);

    private static NewStreamEvent Record(IDomainEvent fact, string siteId, string stream, DateTimeOffset now) =>
        DomainEventRecord.Create(fact, "urn:quality:" + stream, $"{siteId}:{stream}", now);
}
