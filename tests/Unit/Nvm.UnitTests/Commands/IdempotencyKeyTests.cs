using Nvm.Kernel.Commands;
using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Commands;

public sealed class IdempotencyKeyTests
{
    // A measurement's natural key, per docs/scope.md §7.2:
    // (site_id, equipment_id, unit_id, step_code, device_timestamp, signal_code)
    private static readonly string[] OcvMeasurement =
    [
        "NV1",
        "NOVAVOLT/NV1/AGING/A1/OCV-03",
        "NV1CL16238A00123",
        "OCV2",
        "2026-08-25T03:15:40.001+00:00",
        "OCV",
    ];

    [Fact]
    public void FromNaturalKey_SameFactTwice_ProducesTheSameKey()
    {
        // The whole point. The formation cycler that got no acknowledgement sends the same
        // measurement again 800 ms later, from a different process after a restart, or three days
        // later when a gateway flushes its backlog. All of them have to land on this one value.
        var first = IdempotencyKey.FromNaturalKey(OcvMeasurement);
        var second = IdempotencyKey.FromNaturalKey(OcvMeasurement);

        second.ShouldBe(first);
    }

    [Fact]
    public void FromNaturalKey_KnownFact_ProducesAStableValueAcrossRunsAndMachines()
    {
        // Pinning the literal is what makes "deterministic" mean something beyond this process.
        // If this value ever changes, every row already in the deduplication table stops matching
        // the traffic arriving today, and duplicates start getting through silently.
        var key = IdempotencyKey.FromNaturalKey(OcvMeasurement);

        key.Value.ShouldBe(Guid.Parse("c82fd38e-f3ec-5601-a915-1c8af2eb00a9"));
    }

    [Fact]
    public void FromNaturalKey_OneCharacterDifferent_ProducesADifferentKey()
    {
        var ocv = IdempotencyKey.FromNaturalKey(OcvMeasurement);

        var acir = IdempotencyKey.FromNaturalKey([.. OcvMeasurement[..^1], "ACIR"]);

        acir.ShouldNotBe(ocv);
    }

    [Fact]
    public void FromNaturalKey_PartsThatWouldFlattenToTheSameString_StillProduceDifferentKeys()
    {
        // The failure a naive string.Join('|', parts) would create: ["a|b","c"] and ["a","b|c"] both
        // flatten to "a|b|c", so two different facts derive one key and the deduplication step drops
        // one of them for good. Supplier lot codes are free text from someone else's system, so a
        // separator inside a value is a matter of when, not whether.
        var left = IdempotencyKey.FromNaturalKey("NV1", "ROL|004", "STACK");
        var right = IdempotencyKey.FromNaturalKey("NV1", "ROL", "004|STACK");

        right.ShouldNotBe(left);
    }

    [Fact]
    public void FromNaturalKey_DifferentNamespaces_ProduceDifferentKeys()
    {
        var inRoot = IdempotencyKey.FromNaturalKey(OcvMeasurement);

        var inOther = IdempotencyKey.FromNaturalKey(
            DeterministicGuid.CreateVersion5(IdempotencyKey.NovaVoltNamespace, "ingestion"),
            OcvMeasurement);

        inOther.ShouldNotBe(inRoot);
    }

    [Fact]
    public void NovaVoltNamespace_IsDerivedFromTheDnsNamespaceAndNotInvented()
    {
        // Anyone can recompute this from the RFC's DNS namespace and the domain name. A hard-coded
        // random GUID would behave identically and could never be checked.
        var expected = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "novavolt.example");

        IdempotencyKey.NovaVoltNamespace.ShouldBe(expected);
        IdempotencyKey.NovaVoltNamespace.ShouldBe(Guid.Parse("40e49ff3-f5f7-58a6-85aa-d4db02fa35ce"));
    }

    [Fact]
    public void From_EmptyGuid_IsRejected()
    {
        // An all-zero key is what a forgotten assignment looks like. Accepting it would collapse every
        // command that forgot into one, and only under load.
        Should.Throw<ArgumentException>(() => IdempotencyKey.From(Guid.Empty));
    }

    [Fact]
    public void FromNaturalKey_NoParts_IsRejected()
    {
        Should.Throw<ArgumentException>(() => IdempotencyKey.FromNaturalKey());
    }

    [Fact]
    public void FromNaturalKey_NullPart_IsRejected()
    {
        Should.Throw<ArgumentException>(() => IdempotencyKey.FromNaturalKey("NV1", null!, "STACK"));
    }

    [Fact]
    public void FromNaturalKey_EmptyPart_IsAllowedAndDistinct()
    {
        // An absent optional field is a legitimate part of a natural key, and it must not collide with
        // a key that simply has fewer fields.
        var withEmpty = IdempotencyKey.FromNaturalKey("NV1", "", "STACK");
        var withoutIt = IdempotencyKey.FromNaturalKey("NV1", "STACK");

        withoutIt.ShouldNotBe(withEmpty);
    }
}
