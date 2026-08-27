using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Identity;

public sealed class DeterministicGuidTests
{
    [Fact]
    public void CreateVersion5_SetsTheVersionNibbleToFive()
    {
        // Forgetting the two bit-twiddling lines still yields a deterministic 128-bit value that
        // deduplicates perfectly, so every behavioural test would stay green. What it does not yield
        // is a UUID — and that only surfaces at the boundary, when a PostgreSQL uuid column or an
        // auditor's tool refuses to read data that is already in the store.
        var value = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "novavolt.example");

        // Version lives in the high nibble of byte 6, which is character 14 of the canonical form.
        value.ToString()[14].ShouldBe('5');
    }

    [Fact]
    public void CreateVersion5_SetsTheRfc4122VariantBits()
    {
        var value = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "novavolt.example");

        // Variant 10xx binary, so the nibble at character 19 is one of 8, 9, a, b.
        value.ToString()[19].ShouldBeOneOf('8', '9', 'a', 'b');
    }

    [Theory]
    [InlineData("www.example.com", "2ed6657d-e927-568b-95e1-2665a8aea6a2")]
    [InlineData("python.org", "886313e1-3b8a-5372-9b90-0c9aee199e5d")]
    public void CreateVersion5_MatchesTheValuesOtherImplementationsProduce(string name, string expected)
    {
        // Two published vectors for uuid5 over the DNS namespace. They are the check that this is
        // RFC 4122 version 5 and not merely something self-consistent: byte order, hash input and bit
        // masking all have to be right at once for these to come out.
        var value = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, name);

        value.ShouldBe(Guid.Parse(expected));
    }

    [Fact]
    public void CreateVersion5_SameInputTwice_IsIdentical()
    {
        var first = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "NV1|STACK");
        var second = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "NV1|STACK");

        second.ShouldBe(first);
    }

    [Fact]
    public void CreateVersion7_ForComparison_IsNeverIdentical()
    {
        // Documents why version 5 rather than the newer version 7. A UUID version names an algorithm,
        // not a generation: version 7 mixes in the current time, so it cannot answer "have I seen this
        // fact before". See docs/plans/M1-factory-model-bus.md §C04.1.
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        second.ShouldNotBe(first);
    }
}
