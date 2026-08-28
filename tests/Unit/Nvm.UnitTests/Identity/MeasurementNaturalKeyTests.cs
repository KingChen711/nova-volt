using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Identity;

/// <summary>The identity a measurement derives from itself, and the ways it could fail to.</summary>
/// <remarks>
/// Every assertion here is about one property: <b>different fact, different key; same fact, same
/// key</b>. Losing the first half stores two readings as one and the count comes out short. Losing the
/// second half stores one reading twice and the count comes out long. Neither raises an error
/// anywhere, which is why this is checked rather than argued.
/// </remarks>
public sealed class MeasurementNaturalKeyTests
{
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");

    private static readonly DateTimeOffset Measured =
        new(2026, 8, 28, 7, 15, 30, 500, TimeSpan.Zero);

    [Fact]
    public void TheSameReadingDerivedTwiceGivesTheSameIdentity()
    {
        Key().SourceEventId.ShouldBe(Key().SourceEventId);
    }

    [Fact]
    public void TheIdentityIsAVersionFiveGuidAndNotEmpty()
    {
        // Version 5 because it is derived from its input; version 7 mixes in a clock and would give
        // one measurement a new identity every time it arrived (ADR-010).
        var value = Key().SourceEventId.Value;

        value.ShouldNotBe(Guid.Empty);
        value.Version.ShouldBe(5);
    }

    [Fact]
    public void ChangingAnyOneFieldChangesTheIdentity()
    {
        // Six fields, six assertions, because a derivation that quietly ignored one of them would
        // still pass every "same fact, same key" test in this file.
        var baseline = Key().SourceEventId;

        var variants = new[]
        {
            Key(path: EquipmentPath.Parse("NOVAVOLT/DE1/FORMATION/F1/FORM-01/FORM-01-CH-0142")),
            Key(path: EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0143")),
            Key(unitId: "NV1CL16238A00123"),
            Key(stepCode: "AGE"),
            Key(measured: Measured.AddMilliseconds(1)),
            Key(signalCode: "Formation/Current"),
        };

        variants.Select(variant => variant.SourceEventId).ShouldBeUnique();
        variants.ShouldAllBe(variant => variant.SourceEventId != baseline);
    }

    [Fact]
    public void OneInstantWrittenTwoWaysGivesOneIdentity()
    {
        // The most expensive way to get this wrong, and the least visible. NV1 runs at UTC+7 and DE1
        // observes daylight saving, so the same moment genuinely arrives spelled differently depending
        // on which gateway serialised it. Hashing the spelling instead of the instant gives one
        // measurement two identities, and the deduplication step then reports success on a row it has
        // just written for the second time.
        var utc = Key(measured: new DateTimeOffset(2026, 8, 28, 7, 15, 30, 500, TimeSpan.Zero));
        var haiPhong = Key(measured: new DateTimeOffset(2026, 8, 28, 14, 15, 30, 500, TimeSpan.FromHours(7)));
        var leipzig = Key(measured: new DateTimeOffset(2026, 8, 28, 9, 15, 30, 500, TimeSpan.FromHours(2)));

        // Three spellings, three different offsets, one instant.
        haiPhong.DeviceTimestamp.Offset.ShouldNotBe(utc.DeviceTimestamp.Offset);
        leipzig.DeviceTimestamp.Offset.ShouldNotBe(utc.DeviceTimestamp.Offset);

        utc.SourceEventId.ShouldBe(haiPhong.SourceEventId);
        utc.SourceEventId.ShouldBe(leipzig.SourceEventId);
    }

    [Fact]
    public void SubMillisecondPrecisionIsNotRoundedAway()
    {
        // Sparkplug is millisecond-resolution, but the CSV drop in C15 and anything replayed out of a
        // historian need not be. A format that truncated would merge two readings taken 100
        // microseconds apart into one.
        Key(measured: Measured.AddTicks(1)).SourceEventId
            .ShouldNotBe(Key(measured: Measured).SourceEventId);
    }

    [Fact]
    public void AReadingWithNoUnitAndOneWithAnEmptyUnitAreTheSameReading()
    {
        // Deliberate: "no cell in the channel" and "the cell field was blank" are the same statement
        // from a device, and giving them separate identities would store the same coater reading twice
        // depending on which spelling that shift's gateway used.
        Key(unitId: null).SourceEventId.ShouldBe(Key(unitId: string.Empty).SourceEventId);
    }

    [Fact]
    public void TheKeyRemembersThePlantSeparatelyFromThePath()
    {
        // K3 wants site_id as a field of its own, not something a reader has to slice out of a path.
        Key().SiteId.ShouldBe("NV1");
    }

    [Fact]
    public void APathThatNamesNoPlantCannotHaveMeasuredAnything()
    {
        Should.Throw<ArgumentException>(() => MeasurementNaturalKey.For(
            EquipmentPath.Parse("NOVAVOLT"),
            "FORM",
            "Formation/Voltage",
            Measured));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankStepOrSignalCodeIsRefused(string blank)
    {
        Should.Throw<ArgumentException>(() =>
            MeasurementNaturalKey.For(Channel, blank, "Formation/Voltage", Measured));

        Should.Throw<ArgumentException>(() =>
            MeasurementNaturalKey.For(Channel, "FORM", blank, Measured));
    }

    private static MeasurementNaturalKey Key(
        EquipmentPath? path = null,
        string stepCode = "FORM",
        string signalCode = "Formation/Voltage",
        DateTimeOffset? measured = null,
        string? unitId = null) =>
        MeasurementNaturalKey.For(
            path ?? Channel,
            stepCode,
            signalCode,
            measured ?? Measured,
            unitId);
}
