using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>The whole Phase A path, end to end: bytes in, an identity out.</summary>
public sealed class DeviceReadingIdentityTests
{
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    [Fact]
    public void DecodingTheSamePayloadTwiceGivesTheSameIdentities()
    {
        // The at-least-once case, on real bytes. The device that got no acknowledgement resends
        // exactly these bytes; the gateway flushing a backlog sends them hours later. Both have to
        // land on the identity that is already in the deduplication table.
        var first = KeysFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));
        var second = KeysFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));

        first.ShouldBe(second);
        first.Length.ShouldBe(2);
        first.ShouldBeUnique();
    }

    [Fact]
    public void TwoMetricsOfOneMessageAreTwoMeasurements()
    {
        // Voltage and temperature arrive in one payload, at one instant, from one channel. They differ
        // in the signal code alone — so if that field were dropped from the key, one of the two would
        // vanish at the deduplication step and nothing would report it.
        var keys = NaturalKeysFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));

        keys.Select(key => key.SignalCode).ShouldBe(["Formation/Voltage", "Formation/Temperature"]);
        keys.Select(key => key.DeviceTimestamp).Distinct().Count().ShouldBe(1);
        keys.Select(key => key.SourceEventId).ShouldBeUnique();
    }

    [Fact]
    public void TheStepAndThePlantComeOutOfTheResolvedPath()
    {
        var key = NaturalKeysFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData))[0];

        key.SiteId.ShouldBe("NV1");
        key.StepCode.ShouldBe("FORM");
        key.EquipmentPath.ShouldBe(Channel);
        key.UnitId.ShouldBeNull();
    }

    [Fact]
    public void TheSameReadingFromTwoPlantsIsTwoMeasurements()
    {
        // Multiplant, at the level where it is cheapest to get wrong. Two channels with the same code
        // in two factories reporting the same voltage at the same instant are two facts, and a key
        // that left the plant out would store one of them.
        var reading = Readings(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData))[0];

        var atHaiPhong = reading.NaturalKey(Channel);
        var atLeipzig = reading.NaturalKey(
            EquipmentPath.Parse("NOVAVOLT/DE1/FORMATION/F1/FORM-01/FORM-01-CH-0001"));

        atHaiPhong.SourceEventId.ShouldNotBe(atLeipzig.SourceEventId);
    }

    [Fact]
    public void APlaceThatPerformsNoStepCannotHaveMeasuredAnything()
    {
        // A line is where an edge node lives, not where a reading is taken. NDATA from the node itself
        // is node health, and C11 is what does something with it — it is not a process signal.
        var reading = Readings(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData))[0];

        Should.Throw<ArgumentException>(() =>
            reading.NaturalKey(EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1")));
    }

    private static ImmutableArray<DeviceReading> Readings(byte[] data)
    {
        var birth = SparkplugPayload.DecodeBirth(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        return SparkplugPayload.DecodeData(data, birth.Aliases);
    }

    private static MeasurementNaturalKey[] NaturalKeysFrom(byte[] data) =>
        [.. Readings(data).Select(reading => reading.NaturalKey(Channel))];

    private static Guid[] KeysFrom(byte[] data) =>
        [.. NaturalKeysFrom(data).Select(key => key.SourceEventId.Value)];
}
