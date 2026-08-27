using System.Reflection;
using MassTransit;
using Nvm.Contracts.Events;

namespace Nvm.Bus.CloudEvents;

/// <summary>Installs the CloudEvents stamping filter for every declared event.</summary>
/// <remarks>
/// Written against <see cref="IBusFactoryConfigurator"/> rather than the RabbitMQ one on purpose:
/// stamping is about the message, not about the transport carrying it. That also means a test can run
/// the real filter over the in-memory transport instead of re-implementing what it does — a test that
/// stamps its own headers would pass with the filter deleted.
/// </remarks>
public static class CloudEventsBusConfiguratorExtensions
{
    /// <summary>Stamps every outgoing declared event with its CloudEvents attributes.</summary>
    /// <param name="configurator">The bus being configured.</param>
    /// <param name="applicationName">Which deployable this process is, in kebab-case.</param>
    public static void UseNvmCloudEvents(this IBusFactoryConfigurator configurator, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        var install = typeof(CloudEventsBusConfiguratorExtensions)
            .GetMethod(nameof(InstallFilterFor), BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (var eventType in DeclaredEventTypes.All())
        {
            install.MakeGenericMethod(eventType).Invoke(null, [configurator, applicationName]);
        }
    }

    private static void InstallFilterFor<TEvent>(IBusFactoryConfigurator configurator, string applicationName)
        where TEvent : class, IDomainEvent
    {
        var filter = new CloudEventsSendFilter<TEvent>(applicationName);

        // Both pipes. Publish and send are separate in MassTransit; everything here goes out through
        // Publish, and send is covered so that a direct send to an endpoint is not a hole.
        // Casts are needed because one class implements both filter interfaces; without them the
        // compiler picks the non-generic overload and the message type is lost.
        configurator.ConfigurePublish(pipe => pipe.UseFilter((IFilter<PublishContext<TEvent>>)filter));
        configurator.ConfigureSend(pipe => pipe.UseFilter((IFilter<SendContext<TEvent>>)filter));
    }
}
