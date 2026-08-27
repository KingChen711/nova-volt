using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;

namespace Nvm.FactoryModel;

/// <summary>Wires the FactoryModel Functional Block into a host.</summary>
public static class FactoryModelServiceCollectionExtensions
{
    /// <summary>Loads every model revision and registers everything this block needs.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="seedDirectoryPath">Directory holding the <c>factory-model.r*.json</c> documents.</param>
    /// <exception cref="DirectoryNotFoundException">The seed directory is not there.</exception>
    /// <exception cref="FactoryModelSeedException">A document does not describe a valid plant.</exception>
    /// <remarks>
    /// <para>
    /// The documents are read <b>here</b>, while the container is being built, so a broken file stops
    /// the process before it serves anything. Deferring the read to first use would let a service come
    /// up healthy, accept traffic, and then fail on whichever request happened to need the model
    /// first — turning a configuration mistake into an intermittent runtime fault.
    /// </para>
    /// <para>
    /// <b>The whole directory, not one file.</b> Every revision is loaded, because the one a plant is
    /// about to activate and the one it is running now are usually different documents, and the event
    /// that announces the move has to diff the two. Loading only the newest would make that diff
    /// impossible to compute and staged rollout impossible to express.
    /// </para>
    /// <para>
    /// Command handlers and validators come from the assembly scan in <c>AddNvmKernel</c>, which the
    /// host calls with this assembly. This method registers only what the scan cannot find on its own.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddNvmFactoryModel(this IServiceCollection services, string seedDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IFactoryModelCatalog>(FactoryModelSeed.LoadCatalog(seedDirectoryPath));
        services.TryAddSingleton<IActiveFactoryModel, InMemoryActiveFactoryModel>();

        return services;
    }
}
