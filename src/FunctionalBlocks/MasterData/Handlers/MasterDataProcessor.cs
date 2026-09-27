using System.Collections.Immutable;
using Nvm.Contracts.Events.MasterData;
using Nvm.Contracts.Ports;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;
using Nvm.MasterData.Commands;
using Nvm.MasterData.Entities;

namespace Nvm.MasterData.Handlers;

/// <summary>Mã chuẩn, alias, task đối soát và revision của site, trong transaction của command.</summary>
public interface IMasterDataStore
{
    Task<CanonicalItem?> ItemAsync(string siteId, string kind, string canonicalId, CancellationToken cancellationToken);

    Task AddItemAsync(string siteId, CanonicalItem item, string actorId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Mã chuẩn của alias (mã đã chuẩn hoá); null nếu chưa ánh xạ.</summary>
    Task<string?> AliasAsync(string siteId, string kind, string normalizedCode, CancellationToken cancellationToken);

    Task AddAliasAsync(string siteId, string kind, string normalizedCode, string canonicalId, string reason, string actorId,
        DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> RevisionAsync(string siteId, CancellationToken cancellationToken);

    /// <summary>Tăng revision của site và trả giá trị mới; khoá dòng tới commit.</summary>
    Task<long> BumpRevisionAsync(string siteId, DateTimeOffset at, CancellationToken cancellationToken);

    Task<ReconciliationTask?> TaskForUpdateAsync(string siteId, string taskId, CancellationToken cancellationToken);

    /// <summary>Task mở kiểu "mã lạ" của một mã (mọi chứng từ).</summary>
    Task<IReadOnlyList<string>> OpenTaskIdsForCodeAsync(string siteId, string issueKind, string normalizedCode,
        CancellationToken cancellationToken);

    Task AddTaskAsync(string siteId, string taskId, ReconciliationIssue issue, DateTimeOffset at, CancellationToken cancellationToken);

    Task ResolveTaskAsync(string siteId, string taskId, string resolution, string note, string actorId, DateTimeOffset at,
        CancellationToken cancellationToken);
}

public sealed class MasterDataProcessor(IEventStore events, IMasterDataStore store, TimeProvider clock) :
    ICommandHandler<DefineCanonicalItemCommand, DomainCommandResult>,
    ICommandHandler<MapIdentityAliasCommand, DomainCommandResult>,
    ICommandHandler<AcceptReconciliationTaskCommand, DomainCommandResult>
{
    public async Task<DomainCommandResult> HandleAsync(DefineCanonicalItemCommand command, CancellationToken cancellationToken)
    {
        var id = IdentityCode.Normalize(command.CanonicalId);
        if (await store.ItemAsync(command.SiteId, command.Kind, id, cancellationToken).ConfigureAwait(false) is not null)
        { return DomainCommandResult.Reject(MasterDataReasonCodes.ItemExists, "Mã chuẩn đã tồn tại."); }
        if (await store.AliasAsync(command.SiteId, command.Kind, id, cancellationToken).ConfigureAwait(false) is { } other)
        { return DomainCommandResult.Reject(MasterDataReasonCodes.AliasConflict, $"Mã {id} đang là alias của {other}."); }
        var now = clock.GetUtcNow();
        await store.AddItemAsync(command.SiteId, new CanonicalItem(command.Kind, id, command.Name.Trim(), command.BaseUom?.Trim()),
            command.ActorId, now, cancellationToken).ConfigureAwait(false);
        await store.AddAliasAsync(command.SiteId, command.Kind, id, id, "Mã chuẩn", command.ActorId, now, cancellationToken)
            .ConfigureAwait(false);
        var revision = await store.BumpRevisionAsync(command.SiteId, now, cancellationToken).ConfigureAwait(false);
        var closed = await CloseUnknownCodeTasksAsync(command, command.Kind, id, id, revision, now, cancellationToken)
            .ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, null, revision, $"{id}; đóng {closed} task");
    }

    public async Task<DomainCommandResult> HandleAsync(MapIdentityAliasCommand command, CancellationToken cancellationToken)
    {
        var code = IdentityCode.Normalize(command.ExternalCode);
        var canonical = IdentityCode.Normalize(command.CanonicalId);
        if (await store.ItemAsync(command.SiteId, command.Kind, canonical, cancellationToken).ConfigureAwait(false) is null)
        { return DomainCommandResult.Reject(MasterDataReasonCodes.ItemNotFound, $"Chưa có mã chuẩn {canonical}."); }
        if (await store.AliasAsync(command.SiteId, command.Kind, code, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            // Ánh xạ lại một alias đang dùng sẽ đổi nghĩa dữ liệu cũ: phải là quyết định riêng, không lặng lẽ ghi đè.
            return existing == canonical
                ? DomainCommandResult.Reject(MasterDataReasonCodes.AliasConflict, $"{code} đã trỏ về {canonical}.")
                : DomainCommandResult.Reject(MasterDataReasonCodes.AliasConflict, $"{code} đang trỏ về {existing}.");
        }
        var now = clock.GetUtcNow();
        await store.AddAliasAsync(command.SiteId, command.Kind, code, canonical, command.Reason, command.ActorId, now,
            cancellationToken).ConfigureAwait(false);
        var revision = await store.BumpRevisionAsync(command.SiteId, now, cancellationToken).ConfigureAwait(false);
        var mapped = new IdentityAliasMapped(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.Kind, code, canonical, command.Reason, revision, command.ActorId);
        var resolved = await CloseUnknownCodeTasksAsync(command, command.Kind, code, canonical, revision, now, cancellationToken)
            .ConfigureAwait(false);
        var version = await events.AppendAsync(command.SiteId, $"alias:{command.Kind}:{code}", "identity-alias", 0,
            [DomainEventRecord.Create(mapped, $"urn:identity-alias:{command.Kind}:{code}", $"{command.SiteId}:alias:{code}", now)],
            cancellationToken).ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, mapped.EventId, version,
            $"{code} → {canonical}; đóng {resolved} task");
    }

    public async Task<DomainCommandResult> HandleAsync(AcceptReconciliationTaskCommand command, CancellationToken cancellationToken)
    {
        var task = await store.TaskForUpdateAsync(command.SiteId, command.TaskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        { return DomainCommandResult.Reject(MasterDataReasonCodes.TaskNotFound, "Không tìm thấy task."); }
        if (task.Status != TaskStatuses.Open)
        { return DomainCommandResult.Reject(MasterDataReasonCodes.TaskNotOpen, "Task đã đóng."); }
        if (task.Issue.Kind != ReconciliationIssueKinds.UomMismatch)
        { return DomainCommandResult.Reject(MasterDataReasonCodes.TaskNeedsMapping, "Mã lạ phải được ánh xạ, không chấp nhận suông."); }
        var now = clock.GetUtcNow();
        await store.ResolveTaskAsync(command.SiteId, task.TaskId, TaskResolutions.Accepted, command.Note, command.ActorId, now,
            cancellationToken).ConfigureAwait(false);
        var revision = await store.BumpRevisionAsync(command.SiteId, now, cancellationToken).ConfigureAwait(false);
        var fact = new ReconciliationTaskResolved(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId, task.TaskId,
            TaskResolutions.Accepted, command.Note, revision, command.ActorId);
        var version = await events.AppendAsync(command.SiteId, TaskStream(task.TaskId), TaskStreamType, 1,
            [DomainEventRecord.Create(fact, "urn:reconciliation-task:" + task.TaskId, $"{command.SiteId}:{task.TaskId}", now)],
            cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    /// <summary>Đóng mọi task "mã lạ" của <paramref name="code"/> vì mã đó giờ đã giải được.</summary>
    private async Task<int> CloseUnknownCodeTasksAsync(DurableCommand command, string kind, string code, string canonical,
        long revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var issueKind = kind == IdentityKinds.Material ? ReconciliationIssueKinds.UnknownMaterial
            : kind == IdentityKinds.Product ? ReconciliationIssueKinds.UnknownProduct : null;
        if (issueKind is null)
        { return 0; }
        var taskIds = await store.OpenTaskIdsForCodeAsync(command.SiteId, issueKind, code, cancellationToken).ConfigureAwait(false);
        foreach (var taskId in taskIds)
        {
            var note = $"{code} → {canonical}";
            await store.ResolveTaskAsync(command.SiteId, taskId, TaskResolutions.Mapped, note, command.ActorId, now,
                cancellationToken).ConfigureAwait(false);
            await events.AppendAsync(command.SiteId, TaskStream(taskId), TaskStreamType, 1, [DomainEventRecord.Create(
                new ReconciliationTaskResolved(DeterministicGuid.CreateVersion5(command.IdempotencyKey.Value, taskId),
                    command.OccurredAt, now, command.SiteId, taskId, TaskResolutions.Mapped, note, revision, command.ActorId),
                "urn:reconciliation-task:" + taskId, $"{command.SiteId}:{taskId}", now)], cancellationToken).ConfigureAwait(false);
        }
        return taskIds.Count;
    }

    internal const string TaskStreamType = "reconciliation-task";

    internal static string TaskStream(string taskId) => "reconciliation-task:" + taskId;
}

/// <summary>
/// Port <see cref="IMasterDataReconciliation"/> cho FB khác: giải mã qua alias, mở task tất định. Task mở một lần cho mỗi
/// (sai lệch, chứng từ); mở lại cùng task là no-op.
/// </summary>
public sealed class MasterDataReconciliation(IEventStore events, IMasterDataStore store) : IMasterDataReconciliation
{
    public async Task<ResolvedIdentity?> ResolveAsync(string siteId, string kind, string externalCode,
        CancellationToken cancellationToken)
    {
        var canonical = await store.AliasAsync(siteId, kind, IdentityCode.Normalize(externalCode), cancellationToken)
            .ConfigureAwait(false);
        if (canonical is null)
        { return null; }
        var item = await store.ItemAsync(siteId, kind, canonical, cancellationToken).ConfigureAwait(false);
        return item is null ? null : new ResolvedIdentity(item.CanonicalId, item.BaseUom);
    }

    public async Task<bool> IsAcceptedAsync(string siteId, ReconciliationIssue issue, CancellationToken cancellationToken) =>
        await store.TaskForUpdateAsync(siteId, IdentityCode.TaskId(issue), cancellationToken).ConfigureAwait(false) is
        { Status: TaskStatuses.Resolved, Resolution: TaskResolutions.Accepted };

    public async Task<string> OpenTaskAsync(string siteId, ReconciliationIssue issue, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var taskId = IdentityCode.TaskId(issue);
        if (await store.TaskForUpdateAsync(siteId, taskId, cancellationToken).ConfigureAwait(false) is not null)
        { return taskId; }
        await store.AddTaskAsync(siteId, taskId, issue, at, cancellationToken).ConfigureAwait(false);
        var fact = new ReconciliationTaskOpened(DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace,
                $"novavolt/{siteId}/{taskId}"), at, at, siteId, taskId, issue.Kind, IdentityCode.Normalize(issue.ExternalCode),
            issue.CanonicalId, issue.ReceivedUom, issue.ExpectedUom, issue.SourceDocument, issue.Detail);
        await events.AppendAsync(siteId, MasterDataProcessor.TaskStream(taskId), MasterDataProcessor.TaskStreamType, 0,
            ImmutableArray.Create(DomainEventRecord.Create(fact, "urn:reconciliation-task:" + taskId, $"{siteId}:{taskId}", at)),
            cancellationToken).ConfigureAwait(false);
        return taskId;
    }

    public Task<long> RevisionAsync(string siteId, CancellationToken cancellationToken) =>
        store.RevisionAsync(siteId, cancellationToken);
}
