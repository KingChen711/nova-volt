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

    /// <summary>Database writers one accepted batch may spread its rows across.</summary>
    /// <remarks>
    /// <para>
    /// PostgreSQL serves one connection with one backend process, so a batch written through a
    /// single transaction can use exactly one core however many the host has. Measured at R5: with
    /// one edge node and a strictly sequential flusher, the whole pipeline was serial, the timescale
    /// container sat at ~93% of one core, and end-to-end throughput stopped at 4.141 msg/s while the
    /// gateway was already accepting 5.105 msg/s. Memory tuning did not move it because the limit
    /// was CPU, not cache misses.
    /// </para>
    /// <para>
    /// A production plant reaches the same parallelism for free by having many edge nodes posting at
    /// once. D2 measures one, so the fan-out has to be explicit here instead.
    /// </para>
    /// <para>
    /// Each writer commits its own transaction, so a failure can leave part of a batch committed.
    /// That is safe precisely because dedup is the contract: the gateway retries the whole batch and
    /// the committed rows come back as duplicates rather than as second copies. One is never
    /// dropped and never stored twice, which is what D1 asserts.
    /// </para>
    /// </remarks>
    public int WriterParallelism { get; set; } = 4;

    /// <summary>Rows a batch must exceed before it is worth splitting across writers.</summary>
    public int MinRowsPerWriter { get; set; } = 256;

    /// <summary>Delay handed back in <c>Retry-After</c> when a batch is refused.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How far a device clock may disagree with the gateway's before a reading is flagged.</summary>
    public TimeSpan ClockDriftThreshold { get; set; } = ClockQualityClassifier.DefaultThreshold;

    /// <summary>Directory holding the immutable factory-model documents.</summary>
    public string SeedDirectory { get; set; } = "seed";

    /// <summary>Revision this service reads; null selects the newest published document.</summary>
    public int? Revision { get; set; }

    /// <summary>The CSV file-drop adapter (C15). Off unless a plant has an old machine.</summary>
    public FileDrop.FileDropOptions FileDrop { get; set; } = new();

    /// <summary>Where the original bytes of a dropped export are kept (C12). Off by default.</summary>
    public RawCurves.RawCurveArchiveOptions RawCurveArchive { get; set; } = new();

    /// <summary>Signal codes that name an evaluated result rather than an observation.</summary>
    /// <remarks>
    /// Empty means telemetry only, which is the safe default: the reading is stored either way. List
    /// the code the plant uses for the <b>result</b> — <c>Formation/CapacityResult</c>, not
    /// <c>Formation/Capacity</c> — because this list is the only thing that says a signal carries a
    /// decision. See <see cref="Publishing.PublishedSignals"/> for the boundary from scope.md §5.5.
    /// </remarks>
    public IList<string> PublishedSignals { get; } = [];

    /// <summary>RabbitMQ host. Reached from this service's <c>it-net</c> leg only.</summary>
    public string BusHost { get; set; } = "rabbitmq";

    /// <summary>AMQP port.</summary>
    public ushort BusPort { get; set; } = 5672;

    /// <summary>Broker user. Never <c>guest</c>.</summary>
    public string BusUsername { get; set; } = string.Empty;

    /// <summary>Broker password.</summary>
    public string BusPassword { get; set; } = string.Empty;

    /// <summary>Whether this process should connect to the bus at all.</summary>
    /// <remarks>
    /// False when no result signal is whitelisted or credentials are absent. A migration job has
    /// neither, and a bus it never uses is a dependency that can only fail.
    /// </remarks>
    public bool PublishesToBus =>
        PublishedSignals.Count > 0
        && !string.IsNullOrWhiteSpace(BusUsername)
        && !string.IsNullOrWhiteSpace(BusPassword);

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

        if (WriterParallelism <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:WriterParallelism must be positive.");
        }

        if (MinRowsPerWriter <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MinRowsPerWriter must be positive.");
        }

        if (RetryAfter <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:RetryAfter must be positive.");
        }

        if (ClockDriftThreshold < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:ClockDriftThreshold cannot be negative.");
        }

        if (Revision is < 1)
        {
            throw new InvalidOperationException($"{SectionName}:Revision must be at least 1.");
        }

        FileDrop.Validate();
        RawCurveArchive.Validate();
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
