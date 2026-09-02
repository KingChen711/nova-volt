using System.Globalization;
using Nvm.Kernel.Commands;

namespace Nvm.Kernel.Identity;

/// <summary>Điều gì khiến một measurement là chính nó, và identity được suy ra từ đó.</summary>
/// <remarks>
/// <para>
/// Sáu field của docs/scope.md §7.2:
/// <c>(site_id, equipment_id, unit_id, step_code, device_timestamp, signal_code)</c>. Đó là các field
/// mà sự thật này vốn đã có — không gì được sinh ra thêm, nên cùng một reading đến hai lần, từ một
/// device không nhận được acknowledgement và từ một gateway flush backlog ba giờ sau đó, sẽ tạo ra
/// cùng một <see cref="SourceEventId"/> trên bất kỳ máy nào ở bất kỳ process nào.
/// </para>
/// <para>
/// Dùng chung thay vì chỉ dành riêng cho Sparkplug. File CSV thả vào ở C15 phải đáp xuống cùng một key
/// cho cùng một reading, nếu không một plant gửi batch buổi sáng hai lần — một lần qua MQTT và một lần
/// dưới dạng file ai đó re-upload — sẽ lưu nó hai lần.
/// </para>
/// </remarks>
public sealed record MeasurementNaturalKey
{
    /// <summary>
    /// Cách một instant được viết ra trước khi hash: round-trip, luôn UTC, luôn bảy chữ số phần thập
    /// phân của giây.
    /// </summary>
    /// <remarks>
    /// Field nguy hiểm nhất trong sáu field. <c>07:15:30.5+00:00</c> và <c>14:15:30.5+07:00</c> là
    /// cùng một thời điểm nhưng là hai chuỗi khác nhau, nên hash chúng đúng như lúc chúng đến sẽ cho
    /// một measurement hai identity — và bước khử trùng lặp sau đó báo cáo thành công trên một dòng nó
    /// vừa lưu lần thứ hai. Không có gì báo lỗi; count chỉ đơn giản là cao hơn thực tế.
    /// <para>
    /// NV1 là UTC+7 không có daylight saving còn DE1 thì có áp dụng, nên cả hai cách viết đều thực sự
    /// xảy ra.
    /// </para>
    /// </remarks>
    private const string TimestampFormat = "O";

    private MeasurementNaturalKey(
        string siteId,
        EquipmentPath equipmentPath,
        string? unitId,
        string stepCode,
        DateTimeOffset deviceTimestamp,
        string signalCode,
        IdempotencyKey sourceEventId)
    {
        SiteId = siteId;
        EquipmentPath = equipmentPath;
        UnitId = unitId;
        StepCode = stepCode;
        DeviceTimestamp = deviceTimestamp;
        SignalCode = signalCode;
        SourceEventId = sourceEventId;
    }

    /// <summary>Plant. Không bao giờ null — một measurement thuộc về đúng một plant (K3).</summary>
    public string SiteId { get; }

    /// <summary>Máy đã tạo ra reading.</summary>
    public EquipmentPath EquipmentPath { get; }

    /// <summary>Cell, module hay pack mà reading nói về, khi đã biết.</summary>
    /// <remarks>
    /// Null là bình thường chứ không phải một khoảng trống: một coater báo cáo line speed mà hoàn toàn
    /// không có unit bên dưới, và một formation channel chỉ biết nó đang giữ cell nào sau khi cell đó
    /// đã được nạp vào.
    /// </remarks>
    public string? UnitId { get; }

    /// <summary>Process step, ví dụ <c>FORM</c>.</summary>
    public string StepCode { get; }

    /// <summary>Khi device báo rằng nó đã lấy reading.</summary>
    public DateTimeOffset DeviceTimestamp { get; }

    /// <summary>Cái gì đã được đo, ví dụ <c>Formation/Voltage</c>.</summary>
    public string SignalCode { get; }

    /// <summary>Identity suy ra từ sáu field. Đây là <c>source_event_id</c>.</summary>
    public IdempotencyKey SourceEventId { get; }

    /// <summary>Dựng key và suy ra identity.</summary>
    /// <param name="equipmentPath">Máy. Phải nêu tên một plant, tức tối thiểu ở cấp site.</param>
    /// <param name="stepCode">Process step.</param>
    /// <param name="signalCode">Cái gì đã được đo.</param>
    /// <param name="deviceTimestamp">Khi device báo rằng nó đã đo.</param>
    /// <param name="unitId">Unit dưới máy, khi đã biết.</param>
    /// <exception cref="ArgumentException">
    /// Path không nêu tên plant nào, hoặc step code hay signal code bị rỗng.
    /// </exception>
    public static MeasurementNaturalKey For(
        EquipmentPath equipmentPath,
        string stepCode,
        string signalCode,
        DateTimeOffset deviceTimestamp,
        string? unitId = null)
    {
        ArgumentNullException.ThrowIfNull(equipmentPath);

        // Một path ở cấp enterprise không có plant, và K3 không có chỗ cho một measurement không
        // thuộc về plant nào. Nó cũng sẽ khiến field đầu tiên của mọi key như vậy giống hệt nhau, đây
        // chính là thuộc tính mà phép suy ra không được phép có.
        var siteId = equipmentPath.SiteId
            ?? throw new ArgumentException(
                $"'{equipmentPath.Value}' names no plant, so it cannot have produced a measurement.",
                nameof(equipmentPath));

        if (string.IsNullOrWhiteSpace(stepCode))
        {
            throw new ArgumentException("A measurement needs a step code.", nameof(stepCode));
        }

        if (string.IsNullOrWhiteSpace(signalCode))
        {
            throw new ArgumentException("A measurement needs a signal code.", nameof(signalCode));
        }

        // Thứ tự cố định bởi docs/scope.md §7.2 và không bao giờ được sắp xếp lại: các phần được
        // length-prefixed, nên một thứ tự khác là một key khác, và mọi dòng đã có trong store đều
        // được key hóa theo thứ tự này.
        //
        // Unit id vắng mặt được đưa vào như một chuỗi rỗng thay vì bị bỏ qua. Bỏ phần đó đi sẽ làm
        // ngắn tuple lại, và một key năm phần với một key sáu phần cho cùng một reading là hai
        // identity cho một sự thật.
        var sourceEventId = IdempotencyKey.FromNaturalKey(
            siteId,
            equipmentPath.Value,
            unitId ?? string.Empty,
            stepCode,
            deviceTimestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture),
            signalCode);

        return new MeasurementNaturalKey(
            siteId,
            equipmentPath,
            unitId,
            stepCode,
            deviceTimestamp,
            signalCode,
            sourceEventId);
    }

    /// <summary>Sáu field dưới dạng một dòng, dùng cho log hoặc một thông điệp từ chối.</summary>
    /// <remarks>
    /// Không phải chuỗi được hash. Chuỗi đó được length-prefixed nên không thể mập mờ
    /// (<see cref="IdempotencyKey"/>); còn chuỗi này dành cho một người đọc lý do một message bị từ
    /// chối.
    /// </remarks>
    public override string ToString() =>
        string.Join(
            ' ',
            SiteId,
            EquipmentPath.Value,
            UnitId ?? "-",
            StepCode,
            DeviceTimestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture),
            SignalCode);
}
