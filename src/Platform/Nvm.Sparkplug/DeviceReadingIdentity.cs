using Nvm.Kernel.Identity;

namespace Nvm.Sparkplug;

/// <summary>Gán cho một reading đã decode cái identity mà phần còn lại của hệ thống dùng để deduplicate.</summary>
/// <remarks>
/// Mối nối giữa dữ liệu lấy từ wire và <see cref="MeasurementNaturalKey"/>. Nó nằm ở đây thay vì
/// trong <c>Nvm.Kernel</c> vì key là thứ dùng chung còn Sparkplug reading thì không — C15 xây cùng
/// một key đó từ một dòng CSV, và hai bên phải ra cùng một giá trị cho cùng một reading, nếu không
/// một batch được giao hai lần qua hai route sẽ bị lưu hai lần.
/// </remarks>
public static class DeviceReadingIdentity
{
    /// <summary>Suy ra natural key của một reading được lấy tại một vị trí đã biết trong nhà máy.</summary>
    /// <param name="reading">Reading đã decode.</param>
    /// <param name="equipmentPath">Nơi reading được lấy — path đã resolve, không phải topic.</param>
    /// <param name="unitId">Unit bên dưới máy, khi biết được.</param>
    /// <exception cref="ArgumentException">
    /// Path không đặt tên cho nhà máy nào, hoặc nông hơn một work cell nên không thực hiện bước nào.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Cố tình dùng path <b>đã resolve</b>. Một topic mang device code chứ không mang work cell, nên
    /// nếu keying dựa trên bất cứ thứ gì topic tự sinh ra được sẽ cho ra
    /// <c>NOVAVOLT/NV1/FORMATION/F1/FORM-01-CH-0142</c> — một chỗ không tồn tại — và key sẽ đổi ngay
    /// cái ngày ai đó sửa lại nó.
    /// </para>
    /// <para>
    /// Signal code chính là tên metric đúng như device khai báo. Không chuẩn hoá, không viết hoa: đó
    /// là từ vựng riêng của nhà máy, nó đi thẳng vào <c>ts.process_signal.signal_code</c> nguyên trạng,
    /// và nếu chuẩn hoá được áp ở đây mà không áp ở chỗ kia thì key và dòng đã lưu sẽ không còn khớp
    /// nhau về việc cái gì đã được đo.
    /// </para>
    /// </remarks>
    public static MeasurementNaturalKey NaturalKey(
        this DeviceReading reading,
        EquipmentPath equipmentPath,
        string? unitId = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(equipmentPath);

        var stepCode = ProcessStepCode.FromEquipmentPath(equipmentPath)
            ?? throw new ArgumentException(
                $"'{equipmentPath.Value}' is a {equipmentPath.Kind} and performs no process step, so "
                + "a measurement cannot be attributed to it.",
                nameof(equipmentPath));

        return MeasurementNaturalKey.For(
            equipmentPath,
            stepCode,
            reading.MetricName,
            reading.DeviceTimestamp,
            unitId);
    }
}
