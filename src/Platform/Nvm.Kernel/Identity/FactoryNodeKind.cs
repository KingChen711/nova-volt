namespace Nvm.Kernel.Identity;

/// <summary>
/// Một cấp trong equipment hierarchy ISA-95. Giá trị số <b>chính là</b> độ sâu trong một
/// <see cref="EquipmentPath"/>.
/// </summary>
/// <remarks>
/// <para>
/// Sáu cấp, cố định bởi docs/scope.md §2.1. Mỗi cấp trả lời một câu hỏi khác nhau, do một người khác
/// nhau đặt ra: <see cref="Area"/> là cái một shift supervisor theo dõi, <see cref="Equipment"/> là
/// cái traceability cần, và <see cref="Site"/> là nơi ranh giới bảo mật chạy qua.
/// </para>
/// <para>
/// Các giá trị được gán tường minh vì chúng mang ý nghĩa. Một path có bốn segment mô tả một line, và
/// không gì khác; sự tương ứng đó chính là cái khiến việc treo một thiết bị trực tiếp dưới một site
/// trở nên bất khả thi.
/// </para>
/// </remarks>
public enum FactoryNodeKind
{
    /// <summary>Công ty. Một segment: <c>NOVAVOLT</c>.</summary>
    Enterprise = 1,

    /// <summary>Một plant. Hai segment: <c>NOVAVOLT/NV1</c>. Ranh giới authorization chạy ở đây.</summary>
    Site = 2,

    /// <summary>Một process area. Ba segment: <c>NOVAVOLT/NV1/FORMATION</c>.</summary>
    Area = 3,

    /// <summary>Một production line. Bốn segment: <c>NOVAVOLT/NV1/FORMATION/F1</c>.</summary>
    Line = 4,

    /// <summary>Một station hay máy. Năm segment: <c>…/F1/FORM-01</c>.</summary>
    WorkCell = 5,

    /// <summary>
    /// Cấp mịn nhất mà traceability ghi nhận. Sáu segment: <c>…/FORM-01/FORM-01-CH-0142</c>, một
    /// charging channel trong số một nghìn channel trên một formation machine.
    /// </summary>
    Equipment = 6,
}
