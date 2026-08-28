using System.Globalization;

namespace Nvm.LoadHarness;

/// <summary>How hard to push, for how long, and at what.</summary>
public sealed class LoadHarnessOptions
{
    /// <summary>EMQX service name on <c>ot-net</c>.</summary>
    public string BrokerHost { get; set; } = "emqx";

    /// <summary>MQTT listener port inside the Docker network.</summary>
    public int BrokerPort { get; set; } = 1883;

    /// <summary>The line this harness pretends to be.</summary>
    public string LinePath { get; set; } = "NOVAVOLT/NV1/FORMATION/F1";

    /// <summary>Directory holding the factory-model documents, for the channel list.</summary>
    public string SeedDirectory { get; set; } = "seed";

    /// <summary>Target publish rate, in messages per second. N1 is 5.000.</summary>
    public int Rate { get; set; } = 5_000;

    /// <summary>How long to sustain it. D2 asks for ten minutes.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Maximum QoS 1 publishes awaiting acknowledgement on the node's MQTT session.</summary>
    /// <remarks>
    /// <para>
    /// Concurrency is an in-flight window, not extra MQTT clients. Sparkplug assigns one ordered
    /// session to an edge node, so opening one client per worker would create several incompatible
    /// sequence streams under the same node topic.
    /// </para>
    /// <para>
    /// The window is also the publisher's own throughput ceiling while it is the binding constraint:
    /// at QoS 1 no more than this many publishes are outstanding, so the rate cannot exceed window
    /// divided by acknowledgement latency. A window of 32 held the source to 4.933 msg/s and was
    /// failing D2 on the instrument rather than on the pipeline.
    /// </para>
    /// <para>
    /// It stops being the constraint here. Measured 2026-08-29: 32 gave 4.933 msg/s, 128 gave 5.119
    /// and 256 gave 5.104 - flat, because a broker acknowledges a publisher as soon as it accepts
    /// the message and never waits for subscribers, so widening the window past the receiver's own
    /// rate buys nothing. 256 is kept for headroom against stalls, not for throughput.
    /// </para>
    /// </remarks>
    public int MaxInFlightPublishes { get; set; } = 256;

    /// <summary>Builds options from environment variables, so the container needs no arguments.</summary>
    public static LoadHarnessOptions FromEnvironment()
    {
        var options = new LoadHarnessOptions();

        Set("NVM_LOAD_BROKER_HOST", value => options.BrokerHost = value);
        Set("NVM_LOAD_LINE_PATH", value => options.LinePath = value);
        Set("NVM_LOAD_SEED_DIRECTORY", value => options.SeedDirectory = value);
        Set("NVM_LOAD_BROKER_PORT", value => options.BrokerPort = ParseInt(value));
        Set("NVM_LOAD_RATE", value => options.Rate = ParseInt(value));
        Set("NVM_LOAD_MAX_IN_FLIGHT", value => options.MaxInFlightPublishes = ParseInt(value));
        Set("NVM_LOAD_DURATION_SECONDS", value => options.Duration = TimeSpan.FromSeconds(ParseInt(value)));

        options.Validate();
        return options;
    }

    /// <summary>Refuses settings that could not produce a measurement.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(BrokerHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(LinePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BrokerPort);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Rate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInFlightPublishes);

        if (Duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The run duration must be positive.");
        }
    }

    private static void Set(string name, Action<string> assign)
    {
        var value = Environment.GetEnvironmentVariable(name);

        if (!string.IsNullOrWhiteSpace(value))
        {
            assign(value.Trim());
        }
    }

    private static int ParseInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);
}
