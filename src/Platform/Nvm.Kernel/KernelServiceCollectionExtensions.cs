using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nvm.Kernel.Commands;

namespace Nvm.Kernel;

/// <summary>Registers the command pipeline and the handlers that plug into it.</summary>
public static class KernelServiceCollectionExtensions
{
    /// <summary>Adds the dispatcher, and every command handler found in the given assemblies.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="handlerAssemblies">
    /// Assemblies to scan. Each Functional Block passes its own; the kernel does not go looking
    /// through everything loaded, because a Functional Block that never said it was there should not
    /// be wired up by accident.
    /// </param>
    public static IServiceCollection AddNvmKernel(this IServiceCollection services, params Assembly[] handlerAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handlerAssemblies);

        services.TryAddSingleton<ICommandDispatcher, CommandDispatcher>();

        foreach (var assembly in handlerAssemblies)
        {
            RegisterHandlers(services, assembly);
        }

        return services;
    }

    private static void RegisterHandlers(IServiceCollection services, Assembly assembly)
    {
        var candidates = assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false });

        foreach (var implementation in candidates)
        {
            foreach (var contract in HandlerInterfacesOf(implementation))
            {
                // TryAdd, not Add: registering the same handler twice would make the container return
                // the last one and silently ignore the first, which is how two Functional Blocks end
                // up quietly fighting over one command.
                services.TryAddScoped(contract, implementation);
            }
        }
    }

    private static IEnumerable<Type> HandlerInterfacesOf(Type implementation) =>
        implementation.GetInterfaces()
            .Where(contract => contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(ICommandHandler<,>));
}
