using Nvm.FactoryModel.Entities;

namespace Nvm.FactoryModel.Storage;

/// <summary>Model mà một plant đang chạy, và nó đến từ document revision nào.</summary>
/// <param name="Revision">Document revision mà model này được lấy ra từ đó.</param>
/// <param name="Site">Cây của plant, đúng như nó đứng ở revision đó.</param>
/// <remarks>
/// Revision thuộc về document, còn activation thuộc về plant, nên cặp đôi này phải được ghi lại ở đâu
/// đó — một <see cref="FactorySite"/> đọc ra từ file không biết nó đến từ revision nào của file đó.
/// Giữ hai thứ này đi cùng nhau chính là điều cho phép Hai Phong đứng ở revision 12 trong khi Leipzig
/// vẫn còn ở 11.
/// </remarks>
public sealed record ActiveFactoryModelRevision(int Revision, FactorySite Site)
{
    /// <summary>Plant mà bản ghi này áp dụng.</summary>
    public string SiteId => Site.SiteId;
}

/// <summary>Revision nào của model đang có hiệu lực ở mỗi plant ngay lúc này.</summary>
/// <remarks>
/// <para>
/// Tách riêng khỏi seed file, vì "document cung cấp gì" và "plant đang chạy gì" là hai câu hỏi khác
/// nhau. Một document có thể nằm trên đĩa cả tuần trước khi ai đó activate nó, và hai plant có thể
/// đang chạy model từ hai document khác nhau cùng lúc — đó chính là hình dạng của một staged rollout.
/// </para>
/// <para>
/// Hôm nay là in-memory. Phiên bản thật sự quan trọng sẽ giữ cái này trong database, để một lần
/// restart không đưa mọi plant quay lại đúng những gì seed file tình cờ đang nói.
/// </para>
/// </remarks>
public interface IActiveFactoryModel
{
    /// <summary>Plant đang chạy gì, hoặc null khi chưa có gì được activate ở đó.</summary>
    ActiveFactoryModelRevision? Current(string siteId);

    /// <summary>Đưa một revision vào hiệu lực, nhưng chỉ khi plant chưa thay đổi kể từ lúc nó được đọc.</summary>
    /// <param name="revision">Revision cần đưa vào hiệu lực.</param>
    /// <param name="expectedCurrentRevision">
    /// Số revision mà <see cref="Current"/> đã trả về, hoặc null nếu nó không trả về gì.
    /// </param>
    /// <returns>False khi plant đã thay đổi ở khoảng giữa, và không có gì được ghi.</returns>
    /// <remarks>
    /// <para>
    /// Compare-and-swap thay vì một phép ghi bình thường, vì quyết định và ghi là hai bước và có thể
    /// có chuyện xảy ra ở giữa. Hai activation ập đến cùng lúc — một scheduled rollout và một kỹ sư
    /// bấm nút — cả hai đều đọc revision 2, cả hai đều thấy revision của mình mới hơn, và cả hai đều
    /// ghi. Plant sẽ dừng lại ở bất kỳ cái nào xong sau cùng, và hai event được phát ra đều tuyên bố
    /// đã đưa nó tiến lên từ 2. Một consumer đang xây lại cache của nó từ hai event đó không thể biết
    /// cây nào plant thực sự đang chạy.
    /// </para>
    /// <para>
    /// Số revision kiêm luôn vai trò của version token, nên không có gì thêm để lưu. Đây chính là
    /// optimistic concurrency mà event store dùng với <c>expectedVersion</c> ở M5; gặp lại nó ở đây
    /// trước là có chủ đích.
    /// </para>
    /// </remarks>
    bool TryActivate(ActiveFactoryModelRevision revision, int? expectedCurrentRevision);
}
