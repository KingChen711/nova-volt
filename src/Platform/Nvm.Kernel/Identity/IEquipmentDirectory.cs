namespace Nvm.Kernel.Identity;

/// <summary>Trả lời một chỗ trong plant có tồn tại hay không, và một machine code nằm ở đâu.</summary>
/// <remarks>
/// <para>
/// Khai báo ở đây thay vì trong Functional Block trả lời câu hỏi đó, vì các caller không phải là
/// Functional Block. Ingestion và edge gateway cần biến một code khắc trên máy thành một
/// <see cref="EquipmentPath"/>, và K8 không cho phép chúng vươn vào <c>Nvm.FactoryModel</c> để làm
/// việc đó. Block hiện thực interface này; code Platform phụ thuộc vào câu hỏi, không phụ thuộc vào
/// câu trả lời.
/// </para>
/// <para>
/// Nó báo cáo cái đang có hiệu lực <b>ngay bây giờ</b>, đây là câu trả lời đúng cho một message vừa
/// đến từ shop floor và là câu trả lời sai khi đọc lịch sử. Một work cell bị scrap năm ngoái vắng mặt
/// ở đây và vẫn được nêu tên bởi mọi bản ghi traceability viết ra trong lúc nó còn tồn tại — nên caller
/// diễn giải các bản ghi cũ phải đi tới revision đang có hiệu lực lúc đó, không phải đi tới đây.
/// </para>
/// </remarks>
public interface IEquipmentDirectory
{
    /// <summary>Plant hiện có một node đúng tại path này hay không.</summary>
    /// <param name="path">Path cần tìm.</param>
    bool Contains(EquipmentPath path);

    /// <summary>Tìm máy được biết đến bằng <paramref name="deviceCode"/> trên một line cho trước.</summary>
    /// <param name="line">Line mà device báo cáo dưới đó.</param>
    /// <param name="deviceCode">Code mà device tự gọi mình, ví dụ <c>FORM-01-CH-0142</c>.</param>
    /// <returns>Path đầy đủ, hoặc null khi line không có device như vậy.</returns>
    /// <remarks>
    /// <para>
    /// Hai cấp được tìm kiếm, và đó là toàn bộ lý do việc này không thể làm bằng string concatenation.
    /// Một device trên wire đôi khi là một work cell — <c>STACK-01</c> treo trực tiếp dưới line
    /// <c>L1</c> — và đôi khi là một thiết bị bên trong một work cell, như <c>FORM-01-CH-0142</c> bên
    /// trong <c>FORM-01</c>. Topic không mang gợi ý nào cho biết là trường hợp nào, nên phải hỏi model.
    /// </para>
    /// <para>
    /// Giới hạn phạm vi ở một line thay vì cả một plant vì code lặp lại: NV1 và DE1 đều có một
    /// <c>MLOAD-01</c>, và một lookup theo code trên toàn plant sẽ trả lời bằng bất cứ cái nào nó
    /// index sau cùng.
    /// </para>
    /// </remarks>
    EquipmentPath? FindDevice(EquipmentPath line, string deviceCode);
}
