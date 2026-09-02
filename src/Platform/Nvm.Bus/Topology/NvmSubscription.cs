using System.Reflection;
using MassTransit;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;

namespace Nvm.Bus.Topology;

/// <summary>Một thứ mà consumer đã yêu cầu: một exchange, và một pattern để khớp trên đó.</summary>
/// <param name="Exchange">Context exchange, ví dụ <c>nvm.factory-model</c>.</param>
/// <param name="RoutingKey">Binding pattern, ví dụ
/// <c>nvm.*.factory-model.revision-activated.v1</c>.</param>
/// <remarks>
/// Tách khỏi <see cref="NvmConsumerDefinition{TConsumer}"/> để những gì một consumer subscribe vào có
/// thể được assert mà không cần tới broker. Binding là nửa topology fail một cách âm thầm — một
/// pattern sai tạo ra một queue vẫn tồn tại, vẫn được bind, không báo lỗi gì, và cứ rỗng mãi — nên đây
/// là nửa cần nhất một test có thể chuyển màu đỏ.
/// </remarks>
public sealed record NvmSubscription(string Exchange, string RoutingKey)
{
    /// <summary>Tính ra một consumer subscribe vào cái gì, từ các event mà nó xử lý.</summary>
    /// <param name="consumerType">Class consumer.</param>
    /// <exception cref="InvalidOperationException">
    /// Consumer không xử lý gì mang <see cref="EventContractAttribute"/>, nên không có routing key nào
    /// để bind theo.
    /// </exception>
    /// <remarks>
    /// Được suy ra từ các interface <c>IConsumer&lt;T&gt;</c> thay vì khai báo lần thứ hai bên cạnh
    /// chúng. Một consumer bắt đầu xử lý event thứ hai sẽ nhận binding của nó từ đúng lần sửa đã thêm
    /// interface đó, và không thể nào lại đang subscribe vào thứ mà nó không còn xử lý nữa.
    /// </remarks>
    public static IReadOnlyList<NvmSubscription> Of(Type consumerType)
    {
        ArgumentNullException.ThrowIfNull(consumerType);

        var subscriptions = ConsumedEventTypes(consumerType)
            .Select(EventTypeName.Of)
            .Select(eventType => new NvmSubscription(
                NvmTopology.ExchangeFor(eventType),

                // Mọi site, không phải một site. Một probe hay một projection muốn biết sự việc bất kể
                // xảy ra ở đâu; một service chỉ chạy trong một nhà máy thì gọi
                // NvmTopology.BindingForEvent thay vào đó, và lựa chọn đó chính là toàn bộ việc cách ly
                // đa nhà máy (multiplant isolation) ở phía consume.
                NvmTopology.BindingForEventAtEverySite(eventType)))
            .ToArray();

        return subscriptions.Length > 0
            ? subscriptions
            : throw new InvalidOperationException(
                $"Consumer '{consumerType.Name}' handles no event carrying [EventContract], so no "
                + "binding can be derived for it. Either it consumes a message that is not a domain "
                + "event — which does not belong on this bus — or the contract is missing its attribute.");
    }

    private static IEnumerable<Type> ConsumedEventTypes(Type consumerType) =>
        consumerType
            .GetInterfaces()
            .Where(contract => contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IConsumer<>))
            .Select(contract => contract.GetGenericArguments()[0])
            .Where(message => message.IsAssignableTo(typeof(IDomainEvent))
                && message.GetCustomAttribute<EventContractAttribute>() is not null);
}
