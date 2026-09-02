using System.Globalization;
using Nvm.Contracts.Events.FactoryModel;
using Nvm.Kernel.Commands;

namespace Nvm.FactoryModel.Commands;

/// <summary>Đưa một revision đã publish của factory model vào hiệu lực tại một plant.</summary>
/// <param name="IdempotencyKey">Ý định này là gì. Xây nó bằng <see cref="KeyFor"/>.</param>
/// <param name="SiteId">Plant, ví dụ <c>NV1</c>.</param>
/// <param name="Revision">
/// Document nào trong catalog sẽ được đưa vào hiệu lực.
/// </param>
/// <remarks>
/// <para>
/// Revision thuộc về <b>document</b>, không thuộc về plant, còn activation thì tính theo từng plant.
/// Hai sự thật đó cộng lại là thứ cho phép một staged rollout: một model mới có thể được áp dụng ở Hải
/// Phòng trong khi Leipzig vẫn chạy model trước đó, và hai plant được phân biệt bằng việc mỗi bên đang
/// ở document nào.
/// </para>
/// <para>
/// <paramref name="Revision"/> chọn ra đúng một document bất biến trong
/// <see cref="Storage.IFactoryModelCatalog"/>, nơi giữ mọi revision từng tồn tại chứ không chỉ revision
/// đang có hiệu lực. Document không bao giờ bị ghi đè, nên nêu tên một revision sẽ trỏ đúng về cái cây
/// mà caller đã đọc — "tôi đã xem revision 12 và tôi muốn activate đúng nó" là một phát biểu mà handler
/// có thể kiểm chứng chứ không phải tin suông.
/// </para>
/// <para>
/// Handler sau đó hỏi ba câu trước khi ghi: catalog có giữ revision này không, document đó có mô tả
/// đúng plant này không, và nó có đưa plant <b>tiến lên</b> so với revision đang có hiệu lực ở đó hiện
/// tại hay không. Bản thân việc ghi là một compare-and-swap so với chính current revision đó, nên hai
/// lần activate đua nhau cho cùng một plant không thể cùng thành công. Activate một plant model mà
/// chưa ai đọc qua chính là cách một work cell đã ngừng hoạt động tái xuất hiện trên sàn nhà máy.
/// </para>
/// </remarks>
public sealed record ActivateFactoryModelRevisionCommand(
    IdempotencyKey IdempotencyKey,
    string SiteId,
    int Revision) : ICommand<FactoryModelRevisionActivated>
{
    /// <summary>Suy ra idempotency key cho việc activate một revision tại một plant.</summary>
    /// <remarks>
    /// Natural key nằm ngay cạnh command mà nó định danh, để mọi caller đều suy ra cùng một giá trị
    /// thay vì mỗi nơi tự bịa một cái. Activate revision 12 tại NV1 là một ý định duy nhất dù được yêu
    /// cầu bao nhiêu lần — một lần retry sau timeout phải cho ra đúng key với lần thử đã timeout, nếu
    /// không model sẽ bị activate hai lần và hai event được phát ra cho một thay đổi.
    /// </remarks>
    public static IdempotencyKey KeyFor(string siteId, int revision) =>
        IdempotencyKey.FromNaturalKey(
            "factory-model",
            "revision-activated",
            siteId,
            revision.ToString(CultureInfo.InvariantCulture));
}
