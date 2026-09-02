namespace Nvm.Bus.Topology;

/// <summary>Khai báo queue mà một consumer đọc từ đó: nó thuộc context nào, và vai trò của nó là gì.</summary>
/// <param name="context">Bounded context, ví dụ <c>factory-model</c>.</param>
/// <param name="role">Consumer này làm việc gì, viết kebab-case, ví dụ <c>cache-updater</c>.</param>
/// <remarks>
/// <para>
/// MassTransit sẵn sàng đặt tên queue theo tên class của consumer. Điều đó tiện lợi được khoảng một
/// tuần, rồi ai đó đổi tên <c>FactoryModelCacheConsumer</c> thành <c>EquipmentPathCacheConsumer</c>
/// trong IDE. Việc đổi tên là đúng, build vẫn xanh, và ở lần deploy kế tiếp service bắt đầu đọc từ một
/// queue rỗng mới trong khi queue cũ vẫn nằm trong broker giữ những message không bao giờ được xử lý.
/// </para>
/// <para>
/// Tên queue là một dữ kiện vận hành — nó xuất hiện trên dashboard, alert và runbook — nên nó được nêu
/// rõ ở đây và chỉ đổi khi có ai đó thực sự muốn đổi. Một consumer không có attribute này sẽ bị từ chối
/// lúc khởi động thay vì được gán một cái tên một cách tình cờ.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BusEndpointAttribute(string context, string role) : Attribute
{
    /// <summary>Bounded context mà consumer này thuộc về.</summary>
    public string Context { get; } = context;

    /// <summary>Consumer làm việc gì.</summary>
    public string Role { get; } = role;
}
