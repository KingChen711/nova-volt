using System.Diagnostics;

namespace Nvm.Sparkplug;

/// <summary>
/// Trace context qua MQTT 5 (M13, ADR-049): W3C <c>traceparent</c> đi trong user property cùng tên. Thiết bị là nguồn
/// không tin cậy, nên gateway chỉ nhận chuỗi đúng định dạng W3C.
/// </summary>
public static class MqttTraceContext
{
    public const string PropertyName = "traceparent";

    /// <summary>Giá trị hợp lệ thì trả lại, không thì null (không lỗi: trace là best-effort, dữ liệu vẫn được nhận).</summary>
    public static string? Accept(string? value) =>
        value is { Length: 55 } && ActivityContext.TryParse(value, null, isRemote: true, out _) ? value : null;
}
