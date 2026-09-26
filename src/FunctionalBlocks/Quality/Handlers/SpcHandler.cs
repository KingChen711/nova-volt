using Nvm.Kernel.Commands;
using Nvm.Quality.Commands;
using Nvm.Quality.Entities;

namespace Nvm.Quality.Handlers;

/// <summary>Lưu và đọc subgroup SPC theo đặc tính.</summary>
public interface ISpcStore
{
    /// <summary>False nếu subgroup đã tồn tại.</summary>
    Task<bool> AddAsync(string siteId, string characteristic, SpcSubgroup subgroup, string actorId,
        CancellationToken cancellationToken);
}

public sealed class RecordSpcSampleHandler(ISpcStore store) : ICommandHandler<RecordSpcSampleCommand, DomainCommandResult>
{
    public async Task<DomainCommandResult> HandleAsync(RecordSpcSampleCommand command, CancellationToken cancellationToken)
    {
        var added = await store.AddAsync(command.SiteId, command.Characteristic,
            new SpcSubgroup(command.SubgroupId, command.OccurredAt, [.. command.Values]), command.ActorId, cancellationToken)
            .ConfigureAwait(false);
        return added
            ? new DomainCommandResult(true, DomainCommandResult.AcceptedCode, null, null, "Đã ghi subgroup.")
            : DomainCommandResult.Reject("SUBGROUP_EXISTS", "Subgroup này đã được ghi.");
    }
}
