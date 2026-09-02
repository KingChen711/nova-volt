namespace Nvm.FactoryModel.Seeding;

/// <summary>Được ném ra khi seed file không mô tả một plant hợp lệ.</summary>
/// <remarks>
/// Luôn fatal lúc startup, không bao giờ có thể phục hồi. Một service chạy trên một factory model mà
/// nó không đọc trọn vẹn được sẽ resolve được một số equipment path và âm thầm thất bại với những cái
/// khác, điều này còn tệ hơn cả việc không khởi động được: những chỗ thiếu sẽ trông như dữ liệu bị
/// thiếu thay vì một file cấu hình sai.
/// </remarks>
public sealed class FactoryModelSeedException : Exception
{
    /// <summary>Tạo exception với một message mô tả file đang sai ở đâu.</summary>
    public FactoryModelSeedException(string message)
        : base(message)
    {
    }

    /// <summary>Tạo exception với một message và lỗi gốc bên dưới.</summary>
    public FactoryModelSeedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Tạo exception không kèm message.</summary>
    public FactoryModelSeedException()
        : base("The factory model seed is not valid.")
    {
    }
}
