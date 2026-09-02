namespace Nvm.Sparkplug;

/// <summary>Ném ra khi một Sparkplug payload không thể đọc được thành một tập device reading.</summary>
/// <remarks>
/// <para>
/// Mọi nhánh trong decoder hoặc là tạo ra một reading, hoặc là throw. Không có nhánh nào trả về ít
/// metric hơn số đã đến, vì caller không có cách nào phân biệt điều đó với việc report-by-exception
/// quyết định là không có gì thay đổi — cả ý nghĩa của RBE là một payload ngắn vốn là chuyện bình
/// thường.
/// </para>
/// <para>
/// Cùng một bài học mà bus đã học hai lần rồi ở M1: <i>không đọc được</i> khác với <i>không có</i>.
/// Ở đó là một CloudEvents header đọc qua <c>as string</c>; ở đây là một metric bị âm thầm bỏ qua.
/// Một consumer bỏ đi những gì nó không hiểu sẽ báo cáo thành công trên dữ liệu mà nó chưa từng thấy.
/// </para>
/// <para>
/// Có thể phục hồi ở mức message, không phải ở mức process. Ingestion trả lời điều này bằng cách đưa
/// payload sang một bên kèm theo lý do — đường file-drop gọi đó là <c>rejected/</c> còn bus gọi đó là
/// <c>_error</c> — rồi tiếp tục với message kế tiếp. Một payload lỗi từ một channel không được phép
/// làm dừng 999 channel còn lại.
/// </para>
/// </remarks>
public class SparkplugDecodeException : Exception
{
    /// <summary>Tạo exception với một message mô tả payload đang sai ở đâu.</summary>
    public SparkplugDecodeException(string message)
        : base(message)
    {
    }

    /// <summary>Tạo exception với một message và lỗi gốc bên dưới.</summary>
    public SparkplugDecodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Tạo exception không kèm message.</summary>
    public SparkplugDecodeException()
        : base("The Sparkplug payload could not be decoded.")
    {
    }
}
