using Microsoft.Extensions.DependencyInjection;
using Nvm.EventStore;

namespace Nvm.Bus.Outbox;

public static class SqlEventOutboxRegistration
{
    /// <summary>Call after AddNvmBus and AddNvmTraceability in a host with a SQL event store.</summary>
    public static IServiceCollection AddNvmSqlEventOutbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IEventOutboxPublisher, MassTransitEventOutboxPublisher>();
        services.AddScoped<SqlEventOutboxDispatcher>();
        services.AddHostedService<SqlEventOutboxWorker>();
        return services;
    }
}
