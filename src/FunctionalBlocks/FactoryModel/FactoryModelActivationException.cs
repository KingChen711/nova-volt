namespace Nvm.FactoryModel;

/// <summary>Được ném ra khi một revision của factory model không thể được đưa vào hiệu lực.</summary>
/// <remarks>
/// Một lời từ chối nghiệp vụ, không phải một request sai định dạng. Command đã đúng hình dạng và câu
/// trả lời vẫn là không — vì plant không có trong document, vì document đã thay đổi kể từ lúc caller
/// đọc nó, hoặc vì revision này sẽ đẩy plant lùi lại phía sau. Được tách riêng khỏi validation để
/// caller phân biệt được "sửa lại request của bạn" với "thế giới không còn ở trạng thái bạn nghĩ".
/// </remarks>
public sealed class FactoryModelActivationException : Exception
{
    /// <summary>Tạo exception với một message mô tả lý do từ chối.</summary>
    public FactoryModelActivationException(string message)
        : base(message)
    {
    }

    /// <summary>Tạo exception với một message và lỗi gốc bên dưới.</summary>
    public FactoryModelActivationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Tạo exception không kèm message.</summary>
    public FactoryModelActivationException()
        : base("The factory model revision could not be activated.")
    {
    }
}
