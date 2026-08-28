namespace Nvm.Sparkplug;

/// <summary>Thrown when a payload refers to a metric by an alias no birth ever declared.</summary>
/// <remarks>
/// <para>
/// Its own type because the answer is specific: ask the edge node for a <b>rebirth</b> (C11), then
/// decode again. Every other decode failure is a bad payload; this one is a payload that is probably
/// fine and a listener that started listening too late — after a reconnect, after a deploy, after
/// the node restarted and renumbered everything.
/// </para>
/// <para>
/// The alternative to throwing is guessing, and guessing here is expensive in a way that hides. An
/// alias means whatever the <i>current</i> birth said it means; a node that restarts may hand 17 to
/// temperature having handed it to voltage an hour ago. Carrying on with the old table writes 3,7 into
/// a temperature column and 31,5 into a voltage column — correct shape, wrong meaning, no error
/// anywhere. Nothing catches it until a process engineer looks at a chart and sees a cell running at
/// 3,7 °C.
/// </para>
/// </remarks>
public sealed class UnknownMetricAliasException : SparkplugDecodeException
{
    /// <summary>Creates the exception for a specific alias.</summary>
    /// <param name="alias">The alias the payload used.</param>
    /// <param name="knownAliasCount">How many aliases the table did hold.</param>
    public UnknownMetricAliasException(ulong alias, int knownAliasCount)
        : base(
            $"Metric alias {alias} is not in the alias table, which holds {knownAliasCount} "
            + "alias(es). The birth that declared it was never seen, or the node has renumbered "
            + "since. Request a rebirth rather than guessing.")
    {
        Alias = alias;
        KnownAliasCount = knownAliasCount;
    }

    /// <summary>Creates the exception with a message describing the unresolvable alias.</summary>
    public UnknownMetricAliasException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying failure.</summary>
    public UnknownMetricAliasException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with no message.</summary>
    public UnknownMetricAliasException()
        : base("A metric alias is not in the alias table.")
    {
    }

    /// <summary>The alias that could not be resolved, when the exception names one.</summary>
    public ulong? Alias { get; }

    /// <summary>How many aliases the table held at the time, when the exception names it.</summary>
    /// <remarks>
    /// Zero is the case worth separating out while reading a log: it means no birth has been seen at
    /// all, not that this one metric slipped through.
    /// </remarks>
    public int? KnownAliasCount { get; }
}
