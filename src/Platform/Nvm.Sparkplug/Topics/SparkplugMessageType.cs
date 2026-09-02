namespace Nvm.Sparkplug.Topics;

/// <summary>Một message Sparkplug dùng để làm gì, lấy từ topic.</summary>
/// <remarks>
/// Payload không nói điều đó. Mọi thứ decoder cần để biết mình đang nhìn vào một khai báo hay một
/// bản cập nhật đều đến từ đây, đó là lý do <c>SparkplugPayload</c> có hai entry point và không phải
/// đoán.
/// </remarks>
public enum SparkplugMessageType
{
    /// <summary><c>NBIRTH</c> — một edge node vừa online và đang tự khai báo.</summary>
    NodeBirth,

    /// <summary><c>NDEATH</c> — broker đã publish last will của node; nó đã biến mất.</summary>
    NodeDeath,

    /// <summary><c>NDATA</c> — các giá trị đã thay đổi trên chính node.</summary>
    NodeData,

    /// <summary><c>NCMD</c> — một lệnh gửi tới node. Yêu cầu rebirth đi theo đường này.</summary>
    NodeCommand,

    /// <summary><c>DBIRTH</c> — một device dưới node đang khai báo các metric của nó.</summary>
    DeviceBirth,

    /// <summary><c>DDEATH</c> — một device dưới node ngừng báo cáo.</summary>
    DeviceDeath,

    /// <summary><c>DDATA</c> — các giá trị đã thay đổi trên một device.</summary>
    DeviceData,

    /// <summary><c>DCMD</c> — một lệnh gửi tới một device.</summary>
    DeviceCommand,
}

/// <summary>Chuyển đổi giữa enum và token xuất hiện trong một topic.</summary>
/// <remarks>
/// Viết tường minh thay vì đi qua <c>Enum.Parse</c>. Các token trên wire là chữ hoa và viết tắt
/// (<c>NBIRTH</c>, không phải <c>NodeBirth</c>), và một phép parse không phân biệt hoa thường sẽ âm
/// thầm chấp nhận <c>nbirth</c> — một topic từ cây Unified Namespace chữ thường, vốn định danh cùng
/// những máy đó theo một quy ước khác (docs/scope.md §7.1). Chấp nhận cả hai cách viết là cách một
/// máy kết thúc với hai identity.
/// </remarks>
public static class SparkplugMessageTypes
{
    /// <summary>Token mà message type này xuất hiện dưới dạng trong một topic.</summary>
    /// <param name="messageType">Message type.</param>
    /// <exception cref="ArgumentOutOfRangeException">Giá trị không phải một message type.</exception>
    public static string Token(this SparkplugMessageType messageType) =>
        messageType switch
        {
            SparkplugMessageType.NodeBirth => "NBIRTH",
            SparkplugMessageType.NodeDeath => "NDEATH",
            SparkplugMessageType.NodeData => "NDATA",
            SparkplugMessageType.NodeCommand => "NCMD",
            SparkplugMessageType.DeviceBirth => "DBIRTH",
            SparkplugMessageType.DeviceDeath => "DDEATH",
            SparkplugMessageType.DeviceData => "DDATA",
            SparkplugMessageType.DeviceCommand => "DCMD",
            _ => throw new ArgumentOutOfRangeException(nameof(messageType)),
        };

    /// <summary>Message type này có định danh một device thay vì chính edge node hay không.</summary>
    /// <param name="messageType">Message type.</param>
    /// <remarks>
    /// Quyết định topic có bao nhiêu cấp, nên đây cũng chính là thứ bắt được một <c>DDATA</c> được
    /// publish mà không có device, hoặc một <c>NDATA</c> được publish kèm device.
    /// </remarks>
    public static bool IsDeviceLevel(this SparkplugMessageType messageType) =>
        messageType
            is SparkplugMessageType.DeviceBirth
            or SparkplugMessageType.DeviceDeath
            or SparkplugMessageType.DeviceData
            or SparkplugMessageType.DeviceCommand;

    /// <summary>Đọc một token từ topic, phân biệt hoa thường.</summary>
    /// <param name="token">Token lấy từ topic.</param>
    /// <param name="messageType">Message type mà nó định danh.</param>
    /// <returns><see langword="false"/> khi token không phải loại nào Sparkplug định nghĩa.</returns>
    public static bool TryParse(string? token, out SparkplugMessageType messageType)
    {
        var parsed = token switch
        {
            "NBIRTH" => SparkplugMessageType.NodeBirth,
            "NDEATH" => SparkplugMessageType.NodeDeath,
            "NDATA" => SparkplugMessageType.NodeData,
            "NCMD" => SparkplugMessageType.NodeCommand,
            "DBIRTH" => SparkplugMessageType.DeviceBirth,
            "DDEATH" => SparkplugMessageType.DeviceDeath,
            "DDATA" => SparkplugMessageType.DeviceData,
            "DCMD" => SparkplugMessageType.DeviceCommand,
            _ => (SparkplugMessageType?)null,
        };

        messageType = parsed ?? default;

        return parsed is not null;
    }
}
