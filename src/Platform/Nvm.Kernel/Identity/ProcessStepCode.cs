namespace Nvm.Kernel.Identity;

/// <summary>Đọc process step mà một máy thực hiện ra từ code của nó.</summary>
/// <remarks>
/// <para>
/// <c>FORM-01</c> là formation cycler 1, <c>STACK-02</c> là stacker 2, <c>AGE-RACK-01</c> là một aging
/// rack. Step là phần trước dấu gạch ngang đầu tiên, và đó là một <b>naming convention của plant</b>,
/// không phải một sự thật do model khai báo — mọi work cell trong
/// <c>deploy/seed/factory-model.r*.json</c> tuân theo nó, và không gì ép buộc nó sẽ luôn đúng như vậy.
/// </para>
/// <para>
/// Nó được dựa vào ở đây vì M2 chưa có routing data. Câu trả lời thật đến từ routing của sản phẩm
/// đang được sản xuất, đó là một bảng trong database và sẽ đến cùng M3; cho đến lúc đó đây là cách duy
/// nhất để nói một reading thuộc về step nào mà không phải bịa ra một lookup không ai maintain.
/// </para>
/// <para>
/// Hậu quả khi nó sai nhỏ hơn vẻ ngoài của nó, và đáng biết trước khi convention thay đổi. Vai trò của
/// nó trong một natural key là giữ hai sự thật khác nhau tách biệt (docs/scope.md §7.2), và một giá
/// trị được suy ra một cách <i>nhất quán</i> làm được việc đó dù nó có phải là cái tên một process
/// engineer sẽ dùng hay không. Nó không được lưu — <c>ts.process_signal</c> không có cột
/// <c>step_code</c> — nên một giá trị sai không làm ô nhiễm gì cả. Cái sẽ hỏng là một key được suy ra
/// theo một cách hôm nay và theo một cách khác vào ngày mai, đó là lý do vì sao việc này nằm ở một chỗ
/// duy nhất.
/// </para>
/// </remarks>
public static class ProcessStepCode
{
    private const char CodeSeparator = '-';

    /// <summary>Step mà một path thực hiện, hoặc null khi path không nêu tên máy nào.</summary>
    /// <param name="path">Một work cell, hoặc một thiết bị bên trong đó.</param>
    /// <returns>
    /// <c>FORM</c> cho <c>NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142</c>; null cho bất kỳ cái
    /// gì cạn hơn một work cell.
    /// </returns>
    /// <remarks>
    /// Lấy từ <b>work cell</b>, không phải từ segment sâu nhất. Một formation channel được gọi là
    /// <c>FORM-01-CH-0142</c> và sẽ tình cờ cho ra cùng một câu trả lời; một vị trí trên aging rack thì
    /// không. Cell là cái máy thực hiện step, còn con của nó là các bộ phận của nó.
    /// </remarks>
    public static string? FromEquipmentPath(EquipmentPath? path)
    {
        if (path is null || path.Kind < FactoryNodeKind.WorkCell)
        {
            return null;
        }

        var workCell = path.Segments[(int)FactoryNodeKind.WorkCell - 1];
        var separator = workCell.IndexOf(CodeSeparator, StringComparison.Ordinal);

        return separator <= 0 ? workCell : workCell[..separator];
    }
}
