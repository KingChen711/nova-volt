using System.Collections.Immutable;
using Nvm.FactoryModel.Entities;

namespace Nvm.FactoryModel.Storage;

/// <summary>Mọi revision của factory model từng tồn tại, dù đang có hiệu lực hay không.</summary>
/// <remarks>
/// <para>
/// <b>Một revision là một document, và document thì không bị sửa.</b> Thêm một charging channel tạo
/// ra một document mới ở revision cao hơn; document trước đó giữ nguyên như cũ. Đó chính là quy tắc
/// K4 đặt ra cho event store, áp dụng cho master data — và đây không phải sự gọn gàng sổ sách. Một
/// traceability record ghi hồi tháng Ba trước nêu tên
/// <c>NOVAVOLT/NV1/FORMATION/F1/FORM-02</c>, và một auditor hỏi cycler đó thuộc line nào chỉ có thể
/// được trả lời bằng document đang có hiệu lực hồi tháng Ba. Ghi đè nó lên và câu trả lời mất luôn.
/// </para>
/// <para>
/// <b>Tách riêng khỏi cái đang có hiệu lực.</b> Đây là thư viện; <see cref="IActiveFactoryModel"/> là
/// tập nào mỗi plant hiện đang mở. Giữ hai thứ tách biệt chính là điều khiến một staged rollout diễn
/// đạt được: NV1 chạy revision 3 trong khi DE1 vẫn ở 1 là hai câu trả lời khác nhau rút ra từ một kệ
/// sách, không phải hai kệ.
/// </para>
/// <para>
/// Catalog ở đây chỉ đọc. Publish một revision mới được thực hiện bằng cách đặt một document mới vào
/// seed directory, đó là vật thay thế tạm thời của M1 cho import path mà M11 sẽ mang tới — và đó là
/// lý do interface không có Add: không gì trong hệ thống đang chạy được phép tự bịa ra một revision.
/// </para>
/// </remarks>
public interface IFactoryModelCatalog
{
    /// <summary>Catalog đang giữ những revision nào, tăng dần. Không bao giờ rỗng.</summary>
    /// <remarks>
    /// Có khoảng trống là hợp lệ. Một catalog giữ 1, 2 và 5 mô tả một plant mà revision 3 và 4 đã được
    /// soạn nhưng chưa từng được publish, và từ chối nạp nó sẽ là bịa ra một quy tắc mà nghiệp vụ
    /// không hề có.
    /// </remarks>
    ImmutableArray<int> Revisions { get; }

    /// <summary>Revision cao nhất trên kệ. Không nhất thiết là cái plant nào đó đang chạy.</summary>
    int LatestRevision { get; }

    /// <summary>Trả về document của một revision, hoặc null khi catalog không giữ nó.</summary>
    /// <param name="revision">Revision mà caller nói rằng nó đã đọc được.</param>
    /// <remarks>
    /// Null thay vì một exception: hỏi về một revision không tồn tại là chuyện bình thường đối với
    /// một caller đang làm việc với thông tin đã cũ, và handler biến nó thành một lời từ chối nêu rõ
    /// những gì đang sẵn có.
    /// </remarks>
    FactoryModelSnapshot? Find(int revision);
}
