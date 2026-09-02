namespace Nvm.Kernel.Commands.Validation;

/// <summary>Một điều sai trên một command, và nó sai ở field nào.</summary>
/// <param name="Field">Thuộc tính gây lỗi, để một màn hình vận hành có thể chỉ đúng vào đó.</param>
/// <param name="Message">Điều gì đang sai, được diễn đạt cho người phải sửa nó.</param>
public sealed record ValidationFailure(string Field, string Message);

/// <summary>
/// Kiểm tra một command có đúng định dạng hay không, trước khi bất cứ điều gì tác động lên nó.
/// </summary>
/// <typeparam name="TCommand">Command được kiểm tra.</typeparam>
/// <remarks>
/// <para>
/// Chỉ kiểm tra hình dạng: field bắt buộc có mặt, số nằm trong khoảng, mã đúng định dạng. Cố ý đồng bộ
/// (synchronous), vì đó là ranh giới giữa việc này và business rule. "Revision phải ít nhất là 1" thuộc
/// về đây; "revision phải cao hơn revision đang có hiệu lực" cần đọc state và thuộc về handler, nơi nó
/// có thể được quyết định bên trong cùng transaction với thay đổi mà nó bảo vệ.
/// </para>
/// <para>
/// Một command không có validator nào được đăng ký sẽ đi qua thẳng. Đó là mặc định có chủ đích: hầu
/// hết command là record mà constructor của nó đã từ chối những giá trị vô lý rồi, và bắt mỗi command
/// đó phải có một validator rỗng chỉ dạy người ta viết validator rỗng.
/// </para>
/// </remarks>
public interface ICommandValidator<in TCommand>
    where TCommand : ICommand
{
    /// <summary>Trả về mọi thứ sai trên command. Rỗng nghĩa là hợp lệ.</summary>
    /// <param name="command">Command cần kiểm tra.</param>
    IEnumerable<ValidationFailure> Validate(TCommand command);
}
