namespace Nvm.Ingestion.RawCurves;

/// <summary>Ai đã archive một raw curve, vì sao, và bản ghi nào bị nó sửa lại.</summary>
/// <remarks>
/// <para>
/// K5 nói một sai sót được sửa bằng một entry bù trừ mang theo lý do và người thực hiện. Bảng archive
/// vốn append-only ngay từ đầu, nên một correction đã là một dòng thứ hai — nhưng dòng thứ hai đó
/// không nói gì về chính nó, và hai dòng cho cùng một channel và interval mà không có giải thích là
/// một chuỗi bằng chứng mà một auditor không thể lần theo được.
/// </para>
/// <para>
/// Một tham số bắt buộc thay vì tùy chọn. Một giá trị mặc định sẽ bị mọi caller không có gì để nói
/// điền vào cho có, và một cột toàn "system" thì không trả lời được câu hỏi nào cả.
/// </para>
/// </remarks>
/// <param name="Actor">
/// Ai gây ra archive này: một người, hoặc một caller tự động có tên như file-drop adapter. Không bao
/// giờ là một service account trơ trụi — "service nào đã ghi" thì đã có sẵn trong object metadata rồi.
/// </param>
/// <param name="Reason">
/// Vì sao các byte này được archive. Với một correction, điều gì đã sai ở bản ghi mà nó thay thế.
/// </param>
/// <param name="SupersedesArchiveId">
/// Bản ghi archive mà cái này sửa lại, hoặc null nếu là bản gốc. Dòng bị superseded và object của nó
/// đều được giữ lại: thay thế bằng chứng nghĩa là thêm một phát biểu mới bên cạnh, không phải xóa cái cũ.
/// </param>
public sealed record RawCurveProvenance(string Actor, string Reason, Guid? SupersedesArchiveId = null)
{
    /// <summary>Kiểm tra hai trường mà database cũng từ chối nhận giá trị rỗng.</summary>
    /// <exception cref="ArgumentException">Thiếu actor hoặc thiếu reason.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);
    }
}
