using System.Reflection;
using Nvm.Contracts.Events;

namespace Nvm.Bus;

/// <summary>Các event mà hệ thống này biết cách đưa lên bus.</summary>
/// <remarks>
/// Được phát hiện bằng cách quét assembly contracts để tìm <see cref="EventContractAttribute"/> thay vì
/// liệt kê ở đâu đó. Thêm một event khi đó chỉ cần một attribute trên record, chứ không phải attribute
/// đó cộng thêm hai chỗ sửa trong một project mà tác giả không có lý do gì để mở ra — và một danh sách
/// duy trì bằng tay là một danh sách sẽ lỗi thời đúng ở event mà không ai nhớ tới.
/// </remarks>
internal static class DeclaredEventTypes
{
    internal static IEnumerable<Type> All() =>
        typeof(IDomainEvent).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && type.IsAssignableTo(typeof(IDomainEvent))
                && type.GetCustomAttribute<EventContractAttribute>() is not null);
}
