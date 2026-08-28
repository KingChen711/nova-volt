using System.Globalization;
using Npgsql;

namespace Nvm.Ingestion;

/// <summary>Runtime limits and the one database endpoint the ingestion bridge may use.</summary>
public sealed class IngestionOptions
{
    /// <summary>Configuration section consumed by this service.</summary>
    public const string SectionName = "NVM_INGEST";

    private const int DefaultPostgresPort = 5432;

    /// <summary>PostgreSQL connection string. Supplied through the environment in containers.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Address inside the container. No host port is published.</summary>
    public string ListenUrl { get; set; } = "http://0.0.0.0:8080";

    /// <summary>Hard limit before protobuf decode allocates object graphs.</summary>
    public int MaxRequestBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Maximum readings accepted in one transaction.</summary>
    public int MaxReadingsPerBatch { get; set; } = 50_000;

    /// <summary>Batches allowed to hold a database transaction at the same time.</summary>
    /// <remarks>
    /// Admission control, not a throughput setting. Without a ceiling, a gateway draining hours of
    /// backlog opens transactions faster than PostgreSQL retires them, and the failure arrives as
    /// connection-pool timeouts spread across every caller rather than as one honest "slow down".
    /// </remarks>
    public int MaxConcurrentBatches { get; set; } = 8;

    /// <summary>Batches allowed to wait for a slot before ingestion starts refusing.</summary>
    public int MaxQueuedBatches { get; set; } = 16;

    /// <summary>Delay handed back in <c>Retry-After</c> when a batch is refused.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How far a device clock may disagree with the gateway's before a reading is flagged.</summary>
    public TimeSpan ClockDriftThreshold { get; set; } = ClockQualityClassifier.DefaultThreshold;

    /// <summary>Builds options, using the repo's separate PostgreSQL variables for local development.</summary>
    public static IngestionOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new IngestionOptions();
        configuration.GetSection(SectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            options.ConnectionString = BuildDevelopmentConnectionString(configuration);
        }

        options.Validate();
        return options;
    }

    /// <summary>Fails before binding a socket when the service could only fail every request.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(ListenUrl);

        if (MaxRequestBytes <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxRequestBytes must be positive.");
        }

        if (MaxReadingsPerBatch <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxReadingsPerBatch must be positive.");
        }

        if (MaxConcurrentBatches <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxConcurrentBatches must be positive.");
        }

        if (MaxQueuedBatches < 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxQueuedBatches cannot be negative.");
        }

        if (RetryAfter <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:RetryAfter must be positive.");
        }

        if (ClockDriftThreshold < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:ClockDriftThreshold cannot be negative.");
        }
    }

    private static string BuildDevelopmentConnectionString(IConfiguration configuration)
    {
        var portText = configuration["NVM_PORT_POSTGRES"];
        var port = string.IsNullOrWhiteSpace(portText)
            ? DefaultPostgresPort
            : int.Parse(portText, NumberStyles.None, CultureInfo.InvariantCulture);

        return new NpgsqlConnectionStringBuilder
        {
            Host = configuration[$"{SectionName}:DatabaseHost"] ?? "localhost",
            Port = port,
            Database = Required(configuration, "NVM_POSTGRES_DB"),
            Username = Required(configuration, "NVM_POSTGRES_USER"),
            Password = Required(configuration, "NVM_POSTGRES_PASSWORD"),
            ApplicationName = "Nvm.Ingestion",
        }.ConnectionString;
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key]
        ?? throw new InvalidOperationException(
            $"Configuration '{key}' is missing. Copy .env.example to .env or set {SectionName}:ConnectionString.");
}
