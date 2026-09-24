using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nvm.CommandStore;
using Nvm.EventStore;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;
using Nvm.Kernel.EventSourcing;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Handlers;
using Nvm.Traceability.Ports;

namespace Nvm.Traceability.Hosting;

public static class TraceabilityRegistration
{
    /// <summary>Register after AddNvmCommandStore; the event store and adapters share its scoped session.</summary>
    public static IServiceCollection AddNvmTraceability(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(provider =>
        {
            var commands = provider.GetRequiredService<SqlCommandStoreOptions>();
            var connectionString = configuration["NVM_EVENT_STORE:ConnectionString"] ?? commands.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString) ||
                !string.Equals(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).DataSource,
                    new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(commands.ConnectionString).DataSource,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString).InitialCatalog,
                    new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(commands.ConnectionString).InitialCatalog,
                    StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Traceability event store must use the command transaction database."); }
            return new SqlEventStoreOptions { ConnectionString = connectionString };
        });
        services.TryAddSingleton(provider => new EventUpcasterChain(provider.GetServices<IEventUpcaster>()));
        services.TryAddScoped<IEventStore, SqlEventStore>();
        services.AddScoped<SqlTraceabilityAdapters>();
        services.AddScoped<IRoutingDirectory>(provider => provider.GetRequiredService<SqlTraceabilityAdapters>());
        services.AddScoped<IUnitGuard>(provider => provider.GetRequiredService<SqlTraceabilityAdapters>());
        services.AddScoped<ISerialReservation>(provider => provider.GetRequiredService<SqlTraceabilityAdapters>());
        services.AddScoped<IDuplicateSerialQuarantine>(provider => provider.GetRequiredService<SqlTraceabilityAdapters>());
        services.AddScoped<TraceabilityCommandProcessor>();
        services.TryAddScoped<ICommandHandler<SerializeUnitCommand, UnitCommandResult>, SerializeUnitHandler>();
        services.TryAddScoped<ICommandHandler<StartStepCommand, UnitCommandResult>, StartStepHandler>();
        services.TryAddScoped<ICommandHandler<CompleteStepCommand, UnitCommandResult>, CompleteStepHandler>();
        services.TryAddScoped<ICommandHandler<RecordMeasurementCommand, UnitCommandResult>, RecordMeasurementHandler>();
        services.TryAddScoped<ICommandValidator<SerializeUnitCommand>, SerializeUnitValidator>();
        services.TryAddScoped<ICommandValidator<StartStepCommand>, StartStepValidator>();
        services.TryAddScoped<ICommandValidator<CompleteStepCommand>, CompleteStepValidator>();
        services.TryAddScoped<ICommandValidator<RecordMeasurementCommand>, RecordMeasurementValidator>();
        services.AddNvmTraceabilityAuthorization();
        return services;
    }
}
