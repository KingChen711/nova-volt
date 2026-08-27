using System.Globalization;
using System.Text;
using Nvm.Kernel.Identity;

namespace Nvm.Kernel.Commands;

/// <summary>
/// The value that answers "have I already handled this one?".
/// </summary>
/// <remarks>
/// <para>
/// Equipment resends when it gets no acknowledgement. A gateway that has been offline flushes its
/// backlog. The bus itself is at-least-once. The same fact therefore arrives two or three times, and
/// that is normal operation rather than a fault (docs/scope.md §7.2).
/// </para>
/// <para>
/// So the key is never generated — it is <b>derived from the fact itself</b>, through a version 5
/// GUID over the natural key. Two deliveries of one measurement produce the same value on any
/// machine, in any process, three days apart.
/// </para>
/// <para>
/// Deduplication happens in two places and both use this: at ingestion, to drop a repeated device
/// message, and inside the command pipeline, because the bus can redeliver too (AGENTS.md K7). The
/// two layers only compose if they key on the same value, which is why the CloudEvents <c>id</c> of
/// an event produced by a command must equal that command's key.
/// </para>
/// </remarks>
public sealed record IdempotencyKey
{
    /// <summary>
    /// Root namespace for every deterministic identifier in this system.
    /// </summary>
    /// <remarks>
    /// Derived rather than invented, so anyone can recompute it: version 5 of the RFC 4122 DNS
    /// namespace over <c>novavolt.example</c>. A hard-coded random GUID would work just as well and
    /// would be impossible to check.
    /// </remarks>
    public static readonly Guid NovaVoltNamespace =
        DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "novavolt.example");

    private const char PartSeparator = '|';
    private const char LengthSeparator = ':';

    private IdempotencyKey(Guid value) => Value = value;

    /// <summary>The key itself.</summary>
    public Guid Value { get; }

    /// <summary>Wraps a key that was already derived elsewhere, for example read back from a message.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public static IdempotencyKey From(Guid value)
    {
        // An all-zero key is what a forgotten assignment looks like, and it would deduplicate every
        // command that forgot into a single one — silently, and only under load.
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key cannot be empty.", nameof(value));
        }

        return new IdempotencyKey(value);
    }

    /// <summary>Derives a key from the parts of a natural key, in the system's root namespace.</summary>
    /// <param name="parts">
    /// The fields that identify the fact, in a fixed order — for a measurement:
    /// site, equipment, unit, step code, device timestamp, signal code.
    /// </param>
    public static IdempotencyKey FromNaturalKey(params string[] parts) =>
        FromNaturalKey(NovaVoltNamespace, parts);

    /// <summary>Derives a key from the parts of a natural key, in an explicit namespace.</summary>
    /// <param name="namespaceId">Namespace to derive within.</param>
    /// <param name="parts">The fields that identify the fact, in a fixed order.</param>
    /// <exception cref="ArgumentException">No parts were given, or one of them is null.</exception>
    public static IdempotencyKey FromNaturalKey(Guid namespaceId, params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Length == 0)
        {
            throw new ArgumentException("A natural key needs at least one part.", nameof(parts));
        }

        return new IdempotencyKey(DeterministicGuid.CreateVersion5(namespaceId, Encode(parts)));
    }

    /// <summary>Returns the key in the standard GUID form.</summary>
    public override string ToString() => Value.ToString();

    /// <summary>
    /// Joins the parts so that one string can only ever have come from one tuple.
    /// </summary>
    /// <remarks>
    /// Each part is written as <c>length:value|</c>. Plain <c>string.Join('|', parts)</c> looks
    /// equivalent and is not: <c>["a|b", "c"]</c> and <c>["a", "b|c"]</c> both flatten to
    /// <c>"a|b|c"</c>, so two different facts derive the same key and one of them disappears at the
    /// deduplication step. Supplier lot codes are free text from someone else's system, so a
    /// separator turning up inside a value is a question of when.
    /// <para>
    /// Prefixing the length makes the encoding injective, which is the property the whole scheme
    /// rests on: different input, different key. Always.
    /// </para>
    /// </remarks>
    private static string Encode(string[] parts)
    {
        var builder = new StringBuilder();

        foreach (var part in parts)
        {
            if (part is null)
            {
                throw new ArgumentException(
                    "A natural key part cannot be null. Pass an empty string when a field is genuinely absent.",
                    nameof(parts));
            }

            builder
                .Append(part.Length.ToString(CultureInfo.InvariantCulture))
                .Append(LengthSeparator)
                .Append(part)
                .Append(PartSeparator);
        }

        return builder.ToString();
    }
}
