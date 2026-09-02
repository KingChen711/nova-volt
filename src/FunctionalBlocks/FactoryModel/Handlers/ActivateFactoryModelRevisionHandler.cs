using System.Collections.Immutable;
using Nvm.Contracts.Events.FactoryModel;
using Nvm.FactoryModel.Commands;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Commands;

namespace Nvm.FactoryModel.Handlers;

/// <summary>Đưa một revision vào hiệu lực tại một plant, và nói ra thứ gì đã thay đổi.</summary>
/// <param name="catalog">Mọi revision từng tồn tại, dù đang có hiệu lực hay không.</param>
/// <param name="active">Mỗi plant hiện đang chạy gì.</param>
/// <param name="clock">Đồng hồ duy nhất (AGENTS.md K1).</param>
/// <remarks>
/// <para>
/// Giữ đúng các quy tắc cần đến state, và không gì khác. Kiểm tra hình dạng xảy ra ở một bước trước
/// đó trong <see cref="ActivateFactoryModelRevisionValidator"/>; deduplication và audit entry được
/// pipeline bọc quanh lệnh gọi này. Phần còn lại là phần chỉ riêng Functional Block này biết.
/// </para>
/// <para>
/// <b>Hai document, không phải một.</b> Chuyển một plant từ revision 2 sang revision 3 nghĩa là phải
/// đọc cả hai: cái nó đang chạy bây giờ và cái nó được yêu cầu chạy. Event mang theo phần khác biệt
/// giữa hai bên, nên handler không thể làm việc chỉ từ một "document hiện tại" duy nhất — hình dạng
/// đó chỉ có thể thông báo một lần activation đầu tiên, và mọi path sẽ mãi mãi trông như vừa được
/// thêm vào.
/// </para>
/// </remarks>
public sealed class ActivateFactoryModelRevisionHandler(
    IFactoryModelCatalog catalog,
    IActiveFactoryModel active,
    TimeProvider clock)
    : ICommandHandler<ActivateFactoryModelRevisionCommand, FactoryModelRevisionActivated>
{
    private readonly IFactoryModelCatalog _catalog = catalog;
    private readonly IActiveFactoryModel _active = active;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc />
    /// <exception cref="FactoryModelActivationException">The revision cannot be put in force.</exception>
    public Task<FactoryModelRevisionActivated> HandleAsync(
        ActivateFactoryModelRevisionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        // Caller nêu tên revision nó đã đọc, và catalog hoặc là giữ document đó hoặc không. Document
        // không bao giờ bị ghi đè, nên một revision resolve được ở đây chính là cái cây caller đã nhìn
        // thấy — đây chính là điều phép kiểm cũ "file có còn nói đúng thứ bạn nghĩ không" từng cố với
        // tới, mà không thể chứng minh được.
        var document = _catalog.Find(command.Revision)
            ?? throw new FactoryModelActivationException(
                $"The catalog does not hold revision {command.Revision}. "
                + $"It holds: {string.Join(", ", _catalog.Revisions)}.");

        var candidate = document.FindSite(command.SiteId)
            ?? throw new FactoryModelActivationException(
                $"Revision {command.Revision} does not describe plant '{command.SiteId}'.");

        var current = _active.Current(command.SiteId);

        // Phải lớn hơn ngặt, không bao giờ bằng. Activate lại cùng một revision sẽ phát ra một event
        // thứ hai tuyên bố một thay đổi chưa hề xảy ra, và mọi consumer sẽ rebuild cache của nó một
        // cách vô ích. Xử lý một sự lặp lại của cùng một ý định là việc của pipeline, không phải của
        // handler này.
        if (current is not null && command.Revision <= current.Revision)
        {
            throw new FactoryModelActivationException(
                $"Plant '{command.SiteId}' is on revision {current.Revision}; "
                + $"revision {command.Revision} would not move it forward.");
        }

        var before = PathsOf(current?.Site);
        var after = PathsOf(candidate);

        // Compare-and-swap so với revision đã đọc ở trên. Hai phép kiểm trước đó nhìn vào state mà một
        // activation khác có thể đã dịch chuyển kể từ lúc đó; nếu thiếu bước này, cả hai đều sẽ vượt
        // qua phép kiểm của mình và cả hai đều ghi, plant sẽ dừng lại ở bất cứ lần nào hoàn thành sau
        // cùng trong khi hai event đều tuyên bố đã đưa nó tiến lên từ cùng một revision.
        if (!_active.TryActivate(new ActiveFactoryModelRevision(command.Revision, candidate), current?.Revision))
        {
            throw new FactoryModelActivationException(
                $"Plant '{command.SiteId}' moved to another revision while revision {command.Revision} "
                + "was being activated. Read the current revision again and decide afresh.");
        }

        return Task.FromResult(new FactoryModelRevisionActivated(
            // Cùng giá trị với key của command. Đây là phép join giúp deduplication ở ingestion và
            // deduplication trong pipeline đồng thuận với nhau về ý nghĩa của "cùng một thứ"; khi hai
            // bên lệch nhau, mỗi tầng key theo một thứ khác nhau và không tầng nào hoạt động đúng.
            EventId: command.IdempotencyKey.Value,
            OccurredAt: _clock.GetUtcNow(),
            SiteId: candidate.SiteId,
            Revision: command.Revision,
            NodeCount: after.Count,
            EquipmentPathsAdded: Difference(after, before),
            EquipmentPathsRemoved: Difference(before, after)));
    }

    private static HashSet<string> PathsOf(FactorySite? site) =>
        site is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : [.. site.Root.Descend().Select(node => node.Path.Value)];

    // Được sắp xếp (sorted), để hai lần chạy trên cùng một thay đổi cho ra các event giống nhau từng
    // byte. Một event mà payload phụ thuộc vào thứ tự hash thì không thể so sánh với golden file, và
    // không thể diff được bởi bất cứ ai đang cố tìm hiểu một revision thực sự đã làm gì.
    //
    // Dùng ImmutableArray thay vì để compiler tự chọn một read-only list cho IReadOnlyList mà contract
    // khai báo. Cả hai đều an toàn ở hiện tại; chỉ một trong hai vẫn an toàn nếu sau này có ai đó gán
    // một List thường vào đây, vì lúc đó sự đảm bảo đến từ chính kiểu dữ liệu thay vì từ cách giá trị
    // được xây dựng tại đúng call site này.
    private static ImmutableArray<string> Difference(HashSet<string> left, HashSet<string> right) =>
        [.. left.Except(right, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
