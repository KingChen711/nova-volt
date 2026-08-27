namespace Nvm.Bus;

/// <summary>How to reach the broker.</summary>
/// <remarks>
/// Connection settings only. Everything about <i>shape</i> — exchange names, routing keys, queue
/// names — lives in <see cref="Topology.NvmTopology"/> and is not configurable, because a topology
/// that differs between environments is a topology nobody can reason about.
/// </remarks>
public sealed class NvmBusOptions
{
    /// <summary>Broker host name. In development, the container published on localhost.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>AMQP port.</summary>
    public ushort Port { get; set; } = 5672;

    /// <summary>Virtual host. One per environment would be a reasonable later refinement.</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>Broker user.</summary>
    /// <remarks>
    /// Never <c>guest</c>. RabbitMQ only accepts <c>guest</c> from the broker's own loopback, so a
    /// service that appears to work with it in one deployment will fail in the next with an
    /// authentication error that looks like a firewall problem.
    /// </remarks>
    public string Username { get; set; } = string.Empty;

    /// <summary>Broker password.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Which deployable this process is, in kebab-case — <c>app-execution</c>, <c>ingestion</c>.
    /// </summary>
    /// <remarks>
    /// The second half of every CloudEvents source URN this process publishes:
    /// <c>urn:novavolt:nv1:app-execution</c>. Source answers "who says so", and it is the first thing
    /// anyone looks at when two services disagree about the same unit — so it has to name a
    /// deployable, not a machine or a class.
    /// </remarks>
    public string ApplicationName { get; set; } = string.Empty;

    /// <summary>Throws when the options cannot describe a reachable broker.</summary>
    /// <exception cref="InvalidOperationException">A required value is missing.</exception>
    public void Validate()
    {
        // Fail while the container is being built, not on the first publish. A missing password
        // surfaces at startup as one clear line, or two hours later as a consumer that never received
        // anything and a broker log nobody was watching.
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException("Bus host is required.");
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            throw new InvalidOperationException(
                "Bus credentials are required. In development they come from .env "
                + "(NVM_RABBITMQ_USER, NVM_RABBITMQ_PASSWORD) — the same file docker-compose reads.");
        }

        if (string.IsNullOrWhiteSpace(ApplicationName))
        {
            throw new InvalidOperationException(
                "Bus application name is required: it becomes the CloudEvents source of every event "
                + "this process publishes.");
        }
    }
}
