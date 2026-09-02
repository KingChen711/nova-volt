using System.Collections.Concurrent;

namespace Nvm.FactoryModel.Storage;

/// <summary>Giữ revision đang active của mỗi plant trong bộ nhớ tiến trình.</summary>
/// <remarks>
/// <para>
/// Mất sạch khi restart, và hai instance sau một load balancer sẽ bất đồng về revision nào đang có
/// hiệu lực. Cả hai điều này đều không chấp nhận được đối với một plant và cả hai đều cần một
/// database — cái này tồn tại để command pipeline có thứ thật để thao tác trước khi có database.
/// </para>
/// <para>
/// Trong một process, compare-and-swap là thật, nên hai activation đua nhau cho cùng một plant sẽ kết
/// thúc với đúng một cái có hiệu lực và bên thua được báo là đã thua. Chừng đó vẫn giữ nguyên khi
/// chuyển sang database; chỉ có tính durable là cần được xây lại ở đó.
/// </para>
/// </remarks>
public sealed class InMemoryActiveFactoryModel : IActiveFactoryModel
{
    private readonly ConcurrentDictionary<string, ActiveFactoryModelRevision> _active =
        new(StringComparer.Ordinal);

    // Chỉ bảo vệ read-compare-write trong TryActivate. Read nằm ngoài nó, đó chính là mục đích của
    // concurrent dictionary: một operator mở model viewer không được phép xếp hàng chờ sau một rollout.
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ActiveFactoryModelRevision? Current(string siteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        return _active.GetValueOrDefault(siteId);
    }

    /// <inheritdoc />
    public bool TryActivate(ActiveFactoryModelRevision revision, int? expectedCurrentRevision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        lock (_gate)
        {
            // Null so sánh bằng null, đó chính xác là trường hợp activation đầu tiên: caller thấy
            // không có revision nào đang có hiệu lực và đang yêu cầu vẫn phải không có gì cả.
            if (_active.GetValueOrDefault(revision.SiteId)?.Revision != expectedCurrentRevision)
            {
                return false;
            }

            _active[revision.SiteId] = revision;

            return true;
        }
    }
}
