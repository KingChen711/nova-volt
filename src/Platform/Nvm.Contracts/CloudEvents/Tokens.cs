using System.Diagnostics.CodeAnalysis;

namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// Quy tắc hình dạng cho từng segment tạo nên một event type, một source URN và một routing key.
/// </summary>
/// <remarks>
/// Gom về một chỗ vì cùng hai hình dạng này xuất hiện trong ba chuỗi khác nhau, và ba bản sao của
/// "segment này có hợp lệ không" là cách chúng bắt đầu mâu thuẫn với nhau.
/// </remarks>
internal static class Tokens
{
    /// <summary>
    /// Fixed vocabulary: ASCII chữ thường, các từ nối nhau bằng một dấu gạch ngang. Ví dụ
    /// <c>traceability</c>, <c>unit-serialized</c>, <c>app-execution</c>.
    /// </summary>
    /// <remarks>
    /// Chữ thường không phải là sở thích về style. Các segment này do ta viết, do máy đọc, và được một
    /// AMQP broker so sánh theo từng byte; cho phép hai cách viết của cùng một từ nghĩa là cho phép hai
    /// routing key trông giống hệt nhau trong mắt người nhưng không bao giờ khớp nhau.
    /// </remarks>
    internal static bool IsFixedVocabulary([NotNullWhen(true)] string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        // Phải mở đầu bằng một chữ cái và kết thúc bằng chữ cái hoặc chữ số, nên '-lot', 'lot-' và
        // '2fast' đều bị từ chối. Một chữ số ở đầu cũng sẽ khiến segment dễ nhầm lẫn với một version.
        if (!char.IsAsciiLetterLower(token[0]) || token[^1] == '-')
        {
            return false;
        }

        for (var index = 1; index < token.Length; index++)
        {
            var character = token[index];

            if (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))
            {
                continue;
            }

            // Một dấu gạch ngang đơn giữa hai từ thì được phép; hai dấu gạch ngang liền nhau thì
            // không, vì nó sống sót qua một lần copy-paste bất cẩn và tạo ra một cách viết thứ hai
            // khác biệt trong âm thầm.
            if (character == '-' && token[index - 1] != '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Một site code ở dạng canonical: chữ cái và chữ số ASCII viết hoa, ví dụ <c>NV1</c>.
    /// </summary>
    /// <remarks>
    /// Cố tình lỏng hơn ba ký tự mà một serial number cho phép. Danh sách site chính thức thuộc về
    /// factory model, không thuộc về một string parser; đây chỉ ép buộc những gì bản thân wire format
    /// cần, tức là một site code không mang separator và không viết thường.
    /// </remarks>
    internal static bool IsSiteCode([NotNullWhen(true)] string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        foreach (var character in token)
        {
            if (!char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
