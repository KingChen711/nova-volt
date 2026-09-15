using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nvm.Kernel.Commands.Idempotency;

namespace Nvm.CommandStore;

/// <summary>Wiring SQL scoped và guard ngăn host Production chạy store RAM.</summary>
public static class CommandStoreRegistration
{
    /// <summary>Đăng ký sau AddNvmKernel; startup guard kiểm cả trường hợp bị đăng ký đè về RAM.</summary>
    public static IServiceCollection AddNvmCommandStore(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        services.TryAddSingleton(environment);
        var options = new SqlCommandStoreOptions
        {
            ConnectionString = configuration["NVM_COMMANDS:ConnectionString"] ?? "",
            AllowVolatileCommands = environment.IsDevelopment(),
        };
        if (environment.IsDevelopment() && string.IsNullOrWhiteSpace(options.ConnectionString)
            && configuration["NVM_MSSQL_APP_PASSWORD"] is { Length: > 0 } password)
        {
            options.ConnectionString = new SqlConnectionStringBuilder
            {
                DataSource = "localhost," + (configuration["NVM_PORT_MSSQL"] ?? "1433"),
                InitialCatalog = "NovaVolt",
                UserID = "nvm_app",
                Password = password,
                Encrypt = true,
                TrustServerCertificate = true,
                ConnectTimeout = 3,
                ApplicationName = "Nvm.Commands",
            }.ConnectionString;
        }
        if (configuration["NVM_COMMANDS:CommandTimeoutSeconds"] is string timeout)
        {
            if (!int.TryParse(timeout, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                || seconds is < 1 or > 300)
            {
                throw new InvalidOperationException("NVM_COMMANDS:CommandTimeoutSeconds must be between 1 and 300.");
            }
            options.CommandTimeoutSeconds = seconds;
        }
        if (!environment.IsDevelopment() && string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("NVM_COMMANDS:ConnectionString is required outside Development; in-memory command storage is forbidden.");
        }

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InMemoryIdempotencyStore>();
        services.AddScoped<SqlCommandSession>();
        services.AddScoped<ProductionUnitContextReader>();
        services.Replace(ServiceDescriptor.Scoped<IIdempotencyStore, SqlIdempotencyStore>());
        services.AddHostedService<CommandStoreGuard>();
        return services;
    }
}

internal sealed class CommandStoreGuard(IServiceScopeFactory scopes, IHostEnvironment environment, SqlCommandStoreOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!environment.IsDevelopment() && (scope.ServiceProvider.GetRequiredService<IIdempotencyStore>() is not SqlIdempotencyStore
            || options.AllowVolatileCommands || string.IsNullOrWhiteSpace(options.ConnectionString)))
        {
            throw new InvalidOperationException("Production requires SQL command storage.");
        }

    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
