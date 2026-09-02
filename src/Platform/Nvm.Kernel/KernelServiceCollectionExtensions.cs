using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Audit;
using Nvm.Kernel.Commands.Idempotency;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.Kernel;

/// <summary>Đăng ký command pipeline và các handler cắm vào nó.</summary>
public static class KernelServiceCollectionExtensions
{
    /// <summary>Thêm dispatcher, các behaviour chuẩn, và mọi handler trong các assembly đã cho.</summary>
    /// <param name="services">Container đang được dựng.</param>
    /// <param name="handlerAssemblies">
    /// Các assembly cần scan. Mỗi Functional Block truyền vào assembly của riêng nó; kernel không đi
    /// tìm khắp mọi thứ đã load, vì một Functional Block chưa từng khai báo sự hiện diện của mình thì
    /// không nên bị wire up một cách tình cờ.
    /// </param>
    public static IServiceCollection AddNvmKernel(this IServiceCollection services, params Assembly[] handlerAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handlerAssemblies);

        // Scoped, không phải singleton. Dispatcher resolve handler và behaviour từ IServiceProvider nó
        // được cấp, và những cái đó là scoped — một dispatcher singleton giữ root provider, provider
        // này từ chối cấp phát một scoped service và chỉ báo điều đó ở lần dispatch thật đầu tiên. Một
        // caller không có scope riêng của mình (một background service, một saga ở M7) sẽ tạo ra một
        // scope, cũng là điều mà mọi request khác đã làm.
        services.TryAddScoped<ICommandDispatcher, CommandDispatcher>();

        // TryAdd: một host đã đăng ký clock của riêng nó sẽ giữ nguyên nó. Đăng ký ở đây dù vậy vẫn
        // giúp kernel tự hoạt động độc lập, và không có code nào bị cám dỗ dùng
        // DateTimeOffset.UtcNow vì "không có TimeProvider nào cả" (AGENTS.md K1).
        services.TryAddSingleton(TimeProvider.System);

        // Hàng tạm thời, cả hai sẽ được thay thế khi có database. TryAdd để một host có thể thay bằng
        // thứ thật đơn giản chỉ bằng cách đăng ký nó trước.
        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.TryAddSingleton<ICommandAuditSink, InMemoryCommandAuditSink>();

        AddStandardBehaviors(services);

        foreach (var assembly in handlerAssemblies)
        {
            RegisterHandlers(services, assembly);
            RegisterValidators(services, assembly);
        }

        return services;
    }

    /// <summary>
    /// Đăng ký ba behaviour, theo đúng thứ tự chúng phải chạy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Container trả về <c>IEnumerable&lt;T&gt;</c> theo thứ tự đăng ký, và dispatcher bọc cái đầu
    /// tiên ở ngoài cùng. Vì vậy thứ tự này <b>chính là</b> pipeline, và đó là một thuộc tính đúng đắn
    /// chứ không phải một sở thích:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Validation</b> ở ngoài cùng, vì đó là check rẻ nhất và là check duy nhất không cần gì
    ///     ngoài bản thân command. Một command sai định dạng bị từ chối mà không cần một round trip
    ///     nào tới deduplication store — điều này quan trọng một khi store đó là một database và một
    ///     device cấu hình sai đang gửi lại rác với tốc độ của line.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Idempotency</b> tiếp theo, để một lần lặp lại không thực thi gì bên dưới nó và chỉ replay
    ///     kết quả lần đầu.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Audit</b> trong cùng, chỉ bọc riêng handler, để trail ghi lại những gì plant đã làm thay
    ///     vì mọi request đã đến.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>Thứ tự này giờ là một yêu cầu đúng đắn, và trước đây thì không phải vậy.</b> Trong lúc store
    /// chỉ ghi nhận một key sau khi handler trả về, hoán đổi hai stage đầu tiên không làm thay đổi gì
    /// quan sát được — một command bị từ chối không để lại dấu vết dù theo cách nào, và chỉ một test
    /// nhắm thẳng vào thứ tự mới bắt được sự hoán đổi đó.
    /// </para>
    /// <para>
    /// Claim protocol đã chấm dứt điều đó. <see cref="IdempotencyBehavior{TCommand, TResult}"/>
    /// giữ trước key <i>trước khi</i> gọi handler, vì ghi nhận lúc thành công không thể ngăn hai command
    /// giống hệt nhau đến cùng lúc và cả hai đều chạy. Vì vậy một command sai định dạng đi đến được
    /// stage này sẽ đánh dấu key của nó là đã bị chiếm, và lần gửi lại đã sửa đúng — mang cùng natural
    /// key — sẽ bị nuốt như một bản trùng lặp. Operator sửa form, nhấn submit, thấy thành công, và
    /// không có gì xảy ra. Validation phải luôn ở ngoài cùng, nếu không đó là cái plant sẽ nhận.
    /// </para>
    /// <para>
    /// Một stage thứ tư thuộc về giữa idempotency và audit một khi có database: transaction cho phép
    /// bản ghi idempotency commit cùng với event mà nó bảo vệ. Nó còn thiếu vì viết một cái rỗng ngay
    /// bây giờ sẽ là dead code, không phải vì thứ tự còn dư chỗ.
    /// </para>
    /// <para>
    /// Đăng ký dưới dạng open generic — <c>typeof(ValidationBehavior&lt;,&gt;)</c> — để một lần đăng ký
    /// bao phủ mọi command type sẽ từng tồn tại.
    /// </para>
    /// </remarks>
    private static void AddStandardBehaviors(IServiceCollection services)
    {
        // Add, không phải TryAdd. TryAdd trên một open generic sẽ thấy đăng ký ICommandBehavior<,>
        // đầu tiên rồi bỏ qua hai cái còn lại, để lại một pipeline chỉ có một stage mà không than phiền gì.
        services.Add(ServiceDescriptor.Scoped(typeof(ICommandBehavior<,>), typeof(ValidationBehavior<,>)));
        services.Add(ServiceDescriptor.Scoped(typeof(ICommandBehavior<,>), typeof(IdempotencyBehavior<,>)));
        services.Add(ServiceDescriptor.Scoped(typeof(ICommandBehavior<,>), typeof(AuditBehavior<,>)));
    }

    private static void RegisterHandlers(IServiceCollection services, Assembly assembly)
    {
        foreach (var implementation in ConcreteTypesOf(assembly))
        {
            foreach (var contract in ClosedInterfacesOf(implementation, typeof(ICommandHandler<,>)))
            {
                // TryAdd, không phải Add: đăng ký cùng một handler hai lần sẽ khiến container trả về
                // cái cuối cùng và âm thầm bỏ qua cái đầu tiên, đây là cách hai Functional Block cuối
                // cùng lặng lẽ tranh giành một command.
                services.TryAddScoped(contract, implementation);
            }
        }
    }

    private static void RegisterValidators(IServiceCollection services, Assembly assembly)
    {
        foreach (var implementation in ConcreteTypesOf(assembly))
        {
            foreach (var contract in ClosedInterfacesOf(implementation, typeof(ICommandValidator<>)))
            {
                // Add, không phải TryAdd: nhiều validator cho một command là chính đáng, để một
                // Functional Block khác có thể thêm một rule vào một command mà nó không sở hữu.
                services.Add(ServiceDescriptor.Scoped(contract, implementation));
            }
        }
    }

    private static IEnumerable<Type> ConcreteTypesOf(Assembly assembly) =>
        assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false });

    private static IEnumerable<Type> ClosedInterfacesOf(Type implementation, Type openGeneric) =>
        implementation.GetInterfaces()
            .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == openGeneric);
}
