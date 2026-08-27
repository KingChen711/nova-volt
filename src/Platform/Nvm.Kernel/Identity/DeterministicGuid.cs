using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Nvm.Kernel.Identity;

/// <summary>
/// Builds RFC 4122 version 5 identifiers: the same namespace and name always produce the same GUID.
/// </summary>
/// <remarks>
/// <para>
/// The version number in a UUID names an algorithm, not a generation. Version 7 is newer and useless
/// here: it mixes in a timestamp, so calling it twice for the same measurement yields two different
/// values. Only versions 3 and 5 are derived from their input, and version 5 hashes with SHA-1 where
/// version 3 uses MD5.
/// </para>
/// <para>
/// SHA-1 is broken as a cryptographic hash and that does not matter here. Nothing is being protected;
/// the hash only spreads input bits evenly across 128 of them. Nobody gains anything by crafting two
/// natural keys that collide. This is why <c>CA5351</c> is switched off in <c>.editorconfig</c>, with
/// the reason written on the line that switches it off.
/// </para>
/// <para>
/// The BCL offers <see cref="Guid.CreateVersion7()"/> and no version 5, so this is hand-written — see
/// docs/plans/M1-factory-model-bus.md §C04.1.
/// </para>
/// </remarks>
public static class DeterministicGuid
{
    /// <summary>The DNS namespace defined by RFC 4122, used as the root of every derived namespace.</summary>
    public static readonly Guid DnsNamespace = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    private const int HashLength = 20;
    private const int GuidLength = 16;
    private const int VersionByte = 6;
    private const int VariantByte = 8;

    /// <summary>Derives a version 5 GUID from a namespace and a name.</summary>
    /// <param name="namespaceId">The namespace the name is interpreted within.</param>
    /// <param name="name">The name, hashed as UTF-8.</param>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification =
            "RFC 4122 defines version 5 as SHA-1 over namespace and name. The hash is a bit mixer here, "
            + "not a security control: nothing is authenticated, and an attacker gains nothing by finding "
            + "two natural keys that collide. Suppressed at this one method so CA5350 keeps guarding every "
            + "other use of SHA-1 in the repository.")]
    public static Guid CreateVersion5(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Big-endian on purpose. .NET lays the first three fields of a Guid out little-endian in
        // memory, so the default byte order is a .NET detail rather than the wire format RFC 4122
        // describes. Hashing the little-endian bytes would still be deterministic, but it would
        // produce different values from every other implementation on earth — and this identifier is
        // meant to be reproducible by whoever audits the data, in whatever language they use.
        Span<byte> namespaceBytes = stackalloc byte[GuidLength];
        namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _);

        var nameBytes = Encoding.UTF8.GetBytes(name);

        Span<byte> hash = stackalloc byte[HashLength];
        SHA1.HashData([.. namespaceBytes, .. nameBytes], hash);

        // Overwrite four bits with the version and two with the variant, as the RFC requires. Skipping
        // this still yields a deterministic 128-bit value that deduplicates perfectly well — and is
        // not a UUID. The damage only appears at the boundary, when a PostgreSQL uuid column or an
        // auditor's tool refuses to read what is already in the store.
        hash[VersionByte] = (byte)((hash[VersionByte] & 0x0F) | 0x50);
        hash[VariantByte] = (byte)((hash[VariantByte] & 0x3F) | 0x80);

        return new Guid(hash[..GuidLength], bigEndian: true);
    }
}
