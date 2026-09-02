namespace Nvm.Simulator.Faults;

/// <summary>Device nào có đồng hồ sai, và sai bao nhiêu.</summary>
/// <remarks>
/// <para>
/// Đồng hồ PLC sai vì những lý do không tự hết giữa các message: pin CMOS hết, không có đường tới
/// NTP server từ tầng OT, board vừa thay vào còn để giờ mặc định của nhà sản xuất. Vì vậy lựa chọn
/// được chốt <b>theo từng device, một lần duy nhất</b>, dựa trên device code, và giữ nguyên suốt
/// vòng đời của nhà máy — cùng một channel là cái bị hỏng cả sáng lẫn chiều.
/// </para>
/// <para>
/// Chỉ <c>device_timestamp</c> bị lệch. Bản thân reading, serial của cell, sequence number và topic
/// đều đúng, vì trên thiết bị thật chúng cũng đúng: cell trong channel không đổi danh tính chỉ vì
/// đồng hồ trên mặt máy bị sai. Đó chính là cái khó của trường hợp này — không có gì trong message
/// trông có vẻ hỏng cả, và cách duy nhất để biết là so nó với một đồng hồ đáng tin, và đó chính là
/// việc của gateway timestamp (C13).
/// </para>
/// </remarks>
public sealed class DeviceClockDrift
{
    /// <summary>Mọi đồng hồ đều đúng, đây là hình ảnh của một nhà máy chưa bị inject fault nào.</summary>
    public static DeviceClockDrift None { get; } = new(0, TimeSpan.Zero);

    private const uint Buckets = 10_000;

    private readonly uint _threshold;

    /// <summary>Tạo fault này.</summary>
    /// <param name="driftedDeviceRate">Tỉ lệ device có đồng hồ sai, từ 0 đến 1.</param>
    /// <param name="magnitude">Sai bao xa. Áp dụng dạng cộng trên một số device và trừ trên số còn lại.</param>
    /// <exception cref="ArgumentOutOfRangeException">Tỉ lệ không phải là một tỉ lệ hợp lệ.</exception>
    public DeviceClockDrift(double driftedDeviceRate, TimeSpan magnitude)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(driftedDeviceRate, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(driftedDeviceRate, 1);

        DriftedDeviceRate = driftedDeviceRate;
        Magnitude = magnitude < TimeSpan.Zero ? magnitude.Negate() : magnitude;
        _threshold = (uint)Math.Round(driftedDeviceRate * Buckets);
    }

    /// <summary>Tỉ lệ device có đồng hồ sai.</summary>
    public double DriftedDeviceRate { get; }

    /// <summary>Đồng hồ sai thì sai bao xa.</summary>
    public TimeSpan Magnitude { get; }

    /// <summary>Đồng hồ của device này lệch bao xa. Bằng 0 nếu đồng hồ device đúng.</summary>
    /// <param name="deviceCode">Device, ví dụ <c>FORM-01-CH-0142</c>.</param>
    public TimeSpan For(string deviceCode)
    {
        ArgumentNullException.ThrowIfNull(deviceCode);

        if (_threshold == 0 || Magnitude == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var hash = StableHash.Of(deviceCode);

        if (hash % Buckets >= _threshold)
        {
            return TimeSpan.Zero;
        }

        // Một lát khác của cùng hash quyết định dấu, nên việc device nào bị lệch và lệch theo
        // hướng nào là độc lập với nhau. Nếu lấy cả hai từ các bit thấp thì mọi đồng hồ lệch sẽ
        // nghiêng cùng một hướng, và một test chỉ từng thấy đồng hồ chạy chậm sẽ không phát hiện
        // được code lỡ giả định rằng device không bao giờ chạy nhanh hơn gateway.
        return (hash >> 20 & 1) == 0 ? Magnitude.Negate() : Magnitude;
    }
}
