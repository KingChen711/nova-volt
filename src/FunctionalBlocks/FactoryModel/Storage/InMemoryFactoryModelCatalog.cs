using System.Collections.Immutable;
using Nvm.FactoryModel.Entities;

namespace Nvm.FactoryModel.Storage;

/// <summary>Giữ mọi revision trong bộ nhớ tiến trình, đọc một lần lúc khởi động.</summary>
/// <remarks>
/// <para>
/// Khác với <see cref="InMemoryActiveFactoryModel"/>, mất cái này khi restart không tốn gì cả: các
/// document nằm trên đĩa và đọc lại chúng tạo ra đúng catalog như cũ. Cái <i>không</i> durable là
/// revision nào mỗi plant đang có hiệu lực — và sự bất đối xứng đó là có chủ đích, vì đó chính xác là
/// ranh giới mà M5 phải dịch chuyển. Xem restart test trong <c>ActivateFactoryModelRevisionTests</c>,
/// nó ghim giới hạn này lại thay vì chỉ để làm một dòng comment.
/// </para>
/// <para>
/// Bất biến sau khi construct. Một revision có thể bị thay thế trong lúc process đang chạy sẽ đưa lại
/// đúng vấn đề mà catalog tồn tại để loại bỏ.
/// </para>
/// </remarks>
public sealed class InMemoryFactoryModelCatalog : IFactoryModelCatalog
{
    private readonly Dictionary<int, FactoryModelSnapshot> _byRevision;

    /// <summary>Xây một catalog từ các document đã được đọc và validate từ trước.</summary>
    /// <param name="revisions">Các document, theo thứ tự bất kỳ.</param>
    /// <exception cref="ArgumentException">
    /// Không có document nào cả, hoặc hai document cùng nhận là một revision. Cả hai đều có nghĩa là
    /// caller đã lắp ráp catalog sai; một plant không có model thì không thể phục vụ, và hai document
    /// ở cùng một revision có nghĩa là câu trả lời cho "cái gì đang có hiệu lực" phụ thuộc vào thứ tự
    /// nạp.
    /// </exception>
    public InMemoryFactoryModelCatalog(IEnumerable<FactoryModelSnapshot> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);

        _byRevision = [];

        foreach (var snapshot in revisions)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (!_byRevision.TryAdd(snapshot.Revision, snapshot))
            {
                throw new ArgumentException(
                    $"Two documents claim revision {snapshot.Revision}.",
                    nameof(revisions));
            }
        }

        if (_byRevision.Count == 0)
        {
            throw new ArgumentException("A catalog needs at least one revision.", nameof(revisions));
        }

        Revisions = [.. _byRevision.Keys.Order()];
    }

    /// <inheritdoc />
    public ImmutableArray<int> Revisions { get; }

    /// <inheritdoc />
    public int LatestRevision => Revisions[^1];

    /// <inheritdoc />
    public FactoryModelSnapshot? Find(int revision) => _byRevision.GetValueOrDefault(revision);
}
