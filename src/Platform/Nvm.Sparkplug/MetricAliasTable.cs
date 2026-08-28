using System.Collections.Frozen;
using System.Collections.Immutable;
using Org.Eclipse.Tahu.Protobuf;

namespace Nvm.Sparkplug;

/// <summary>What a birth declared: which number stands for which metric, and of what type.</summary>
/// <remarks>
/// <para>
/// A formation machine has a thousand channels and six to eight metrics on each. Spelling
/// <c>Formation/Voltage</c> out in every message would be a few hundred bytes of name for four bytes
/// of reading, five thousand times a second, over the narrowest link in the plant. Sparkplug answers
/// that with aliases: the birth says <i>"metric 1 is Formation/Voltage, a Float"</i> once, and every
/// message afterwards sends <c>1</c>.
/// </para>
/// <para>
/// So this table is not a cache. It is the only copy of the meaning of every later message, and it is
/// scoped to <b>one session of one node</b> — a node that reconnects publishes a new birth and is free
/// to hand 1 to something else. C11 is what throws the table away on a new <c>bdSeq</c>; until then it
/// is passed in explicitly, which keeps the lifetime visible rather than hidden in a static.
/// </para>
/// <para>
/// The datatype is kept alongside the name and is not exposed, deliberately: it is the wire's
/// vocabulary, not the plant's, and letting <c>Org.Eclipse.Tahu.Protobuf</c> types out of this
/// assembly is the boundary ADR-026 asks to hold. It is needed internally because
/// <c>int_value</c> carries both <c>Int32</c> and <c>UInt32</c> and only the declaration says which.
/// </para>
/// </remarks>
public sealed class MetricAliasTable
{
    private readonly FrozenDictionary<ulong, MetricDefinition> _byAlias;

    private MetricAliasTable(FrozenDictionary<ulong, MetricDefinition> byAlias)
    {
        _byAlias = byAlias;
        Aliases = [.. byAlias.Keys.Order()];
    }

    /// <summary>The table before any birth has been seen.</summary>
    /// <remarks>
    /// Not a null object that quietly resolves everything: every alias-only metric decoded against it
    /// throws, which is the correct answer to "a data message arrived before its birth".
    /// </remarks>
    public static MetricAliasTable Empty { get; } = new(FrozenDictionary<ulong, MetricDefinition>.Empty);

    /// <summary>The aliases this table can resolve, ascending.</summary>
    /// <remarks>
    /// Precomputed rather than projected on each read — a property that allocates is a property that
    /// gets called in a loop. <see cref="ImmutableArray{T}"/> for the reason in ADR-025.
    /// </remarks>
    public ImmutableArray<ulong> Aliases { get; }

    /// <summary>How many aliases the birth declared.</summary>
    public int Count => _byAlias.Count;

    /// <summary>Looks up the metric name behind an alias.</summary>
    /// <param name="alias">The number the payload used.</param>
    /// <param name="metricName">The declared name, when the alias is known.</param>
    /// <returns><see langword="true"/> when the alias was declared by the birth.</returns>
    public bool TryGetMetricName(ulong alias, out string? metricName)
    {
        if (_byAlias.TryGetValue(alias, out var definition))
        {
            metricName = definition.Name;
            return true;
        }

        metricName = null;
        return false;
    }

    // Frozen rather than a plain Dictionary: built once per birth, then read for every message of the
    // session that follows — the exact shape FrozenDictionary is faster at, and the same choice the
    // factory model's flat index made in M1.
    internal static MetricAliasTable From(IReadOnlyDictionary<ulong, MetricDefinition> definitions) =>
        definitions.Count == 0
            ? Empty
            : new MetricAliasTable(definitions.ToFrozenDictionary());

    internal bool TryResolve(ulong alias, out MetricDefinition definition) =>
        _byAlias.TryGetValue(alias, out definition!);
}

/// <summary>One line of a birth: what a metric is called and what type it carries.</summary>
internal sealed record MetricDefinition(string Name, DataType DataType);
