using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;

namespace Nvm.FactoryModel;

/// <summary>Wires the FactoryModel Functional Block into a host.</summary>
public static class FactoryModelServiceCollectionExtensions
{
    /// <summary>Loads the model document and registers everything this block needs.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="seedFilePath">Full path to <c>factory-model.json</c>.</param>
    /// <exception cref="FileNotFoundException">The seed file is not there.</exception>
    /// <exception cref="FactoryModelSeedException">The seed file does not describe a valid plant.</exception>
    /// <remarks>
    /// <para>
    /// The document is read <b>here</b>, while the container is being built, so a broken file stops
    /// the process before it serves anything. Deferring the read to first use would let a service come
    /// up healthy, accept traffic, and then fail on whichever request happened to need the model
    /// first — turning a configuration mistake into an intermittent runtime fault.
    /// </para>
    /// <para>
    /// Command handlers and validators come from the assembly scan in <c>AddNvmKernel</c>, which the
    /// host calls with this assembly. This method registers only what the scan cannot find on its own.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddNvmFactoryModel(this IServiceCollection services, string seedFilePath)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(FactoryModelSeed.Load(seedFilePath));
        services.TryAddSingleton<IActiveFactoryModel, InMemoryActiveFactoryModel>();

        return services;
    }
}
