using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Audit;
using Nvm.Kernel.Commands.Idempotency;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.Kernel;

/// <summary>Registers the command pipeline and the handlers that plug into it.</summary>
public static class KernelServiceCollectionExtensions
{
    /// <summary>Adds the dispatcher, the standard behaviours, and every handler in the given assemblies.</summary>
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

        // TryAdd: a host that already registered the clock keeps its own. Registering it here anyway
        // means the kernel works on its own, and that no code is ever tempted to reach for
        // DateTimeOffset.UtcNow because "there was no TimeProvider" (AGENTS.md K1).
        services.TryAddSingleton(TimeProvider.System);

        // Stand-ins, both replaced when there is a database. TryAdd so a host can substitute the real
        // thing simply by registering it first.
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
    /// Registers the three behaviours, in the order they must run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The container hands back <c>IEnumerable&lt;T&gt;</c> in registration order, and the dispatcher
    /// wraps the first one outermost. So this order <b>is</b> the pipeline, and it is a correctness
    /// property rather than a preference:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Validation</b> outermost, because it is the cheapest check and the only one that needs
    ///     nothing but the command itself. A malformed command is refused without a single round trip
    ///     to the deduplication store — which matters once that store is a database and a misconfigured
    ///     device is resending rubbish at line rate.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Idempotency</b> next, so a repeat performs nothing below it and replays the first
    ///     result.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Audit</b> innermost, wrapping the handler alone, so the trail records what the plant did
    ///     rather than every request that arrived.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>Where this order stops being a preference and becomes correctness.</b> The current store
    /// records a key only after the handler has returned, so putting validation underneath would still
    /// leave a rejected command unrecorded — the two orders behave the same today, and the swap is
    /// caught by a test on the order itself rather than by a test on behaviour.
    /// </para>
    /// <para>
    /// That changes with the real store. Recording on success cannot stop two identical commands
    /// arriving at once from both running: each looks the key up, each misses, each proceeds. Closing
    /// that needs the key to be <i>claimed</i> on the way in and confirmed on the way out. From the
    /// moment the claim happens first, a malformed command reaching this stage marks its key as taken,
    /// and the corrected resend — which carries the same natural key — is swallowed as a duplicate.
    /// The operator fixes the form, presses submit, sees success, and nothing happens.
    /// </para>
    /// <para>
    /// A fourth stage belongs between idempotency and audit once there is a database: the transaction
    /// that lets the idempotency record commit together with the event it guards. It is missing
    /// because writing an empty one now would be dead code, not because the order has room to spare.
    /// </para>
    /// <para>
    /// Registered as open generics — <c>typeof(ValidationBehavior&lt;,&gt;)</c> — so one registration
    /// covers every command type there will ever be.
    /// </para>
    /// </remarks>
    private static void AddStandardBehaviors(IServiceCollection services)
    {
        // Add, not TryAdd. TryAdd on an open generic would see the first ICommandBehavior<,>
        // registration and skip the other two, leaving a pipeline with one stage and no complaint.
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
                // TryAdd, not Add: registering the same handler twice would make the container return
                // the last one and silently ignore the first, which is how two Functional Blocks end
                // up quietly fighting over one command.
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
                // Add, not TryAdd: several validators for one command is legitimate, so another
                // Functional Block can add a rule to a command it does not own.
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
