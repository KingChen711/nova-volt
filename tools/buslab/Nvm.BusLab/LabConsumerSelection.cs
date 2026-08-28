namespace Nvm.BusLab;

/// <summary>Which of the lab's consumers this run starts.</summary>
/// <remarks>
/// <para>
/// One switch rather than one flag per consumer, because the questions being asked need consumers
/// turned <b>off</b> as much as on. The claim that two queues are independent is only demonstrated
/// by stopping one consumer and watching its queue fill while the other keeps working — with both
/// always running, two log lines are equally explained by two queues and by one queue read twice.
/// </para>
/// <para>
/// The failing consumer is off by default for a different reason: left on, it would move every
/// measurement this lab sees into an error queue permanently, and an error queue that always has
/// messages in it is one nobody reads.
/// </para>
/// </remarks>
public static class LabConsumerSelection
{
    /// <summary>Environment variable holding a comma-separated list of roles.</summary>
    public const string Variable = "NVM_BUS_LAB_CONSUMERS";

    /// <summary>The latest-value cache consumer.</summary>
    public const string Cache = "cache";

    /// <summary>The audit-trail consumer.</summary>
    public const string Audit = "audit";

    /// <summary>The consumer that fails on purpose.</summary>
    public const string Failing = "failing";

    private static readonly string[] Default = [Cache, Audit];

    /// <summary>Whether a role is part of this run.</summary>
    public static bool Includes(string role) => Roles().Contains(role, StringComparer.OrdinalIgnoreCase);

    /// <summary>The roles this run was asked for, for the startup line.</summary>
    public static string Describe() => string.Join(", ", Roles());

    private static string[] Roles() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } configured
            ? configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : Default;
}
