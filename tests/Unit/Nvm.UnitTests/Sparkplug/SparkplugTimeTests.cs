using System.Reflection;
using Nvm.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;
using SparkplugMetric = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Where a reading's timestamp comes from, and what type it is allowed to be.</summary>
public sealed class SparkplugTimeTests
{
    private const ulong PayloadMs = 1787901330500;

    [Fact]
    public void NoTypeInThisAssemblyPutsADateTimeOnADeviceReading()
    {
        // AGENTS.md K2, checked here because no analyzer covers it. NVM002 refuses DateTime across
        // Nvm.Contracts and this assembly is not Nvm.Contracts, so without this test the rule holds
        // everywhere except the one place device clocks actually enter the system.
        //
        // Site DE1 observes daylight saving: one hour every autumn happens twice there. A wall-clock
        // reading with no offset cannot say which of the two it was, telemetry is kept 400 days, and
        // the store is append-only — so the ambiguity would still be in the table, uncorrectable, when
        // somebody came to investigate it.
        var offenders = typeof(DeviceReading).Assembly
            .GetExportedTypes()
            .Where(type => string.Equals(type.Namespace, "Nvm.Sparkplug", StringComparison.Ordinal))
            .SelectMany(MembersMentioningDateTime)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty($"DateTime reaches a reading through: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Control_TheDateTimeWalkFindsOneBuriedInAGeneric()
    {
        // Without this, a walk that looked only at the outermost type — or at no types at all, because
        // the namespace filter was misspelled — would report "no offenders" forever.
        MembersMentioningDateTime(typeof(ReadingWithForbiddenClock))
            .ShouldContain($"{nameof(ReadingWithForbiddenClock)}.{nameof(ReadingWithForbiddenClock.Samples)}");
    }

    [Fact]
    public void AMetricWithoutItsOwnTimestampInheritsThePayloadsOne()
    {
        // Legal and common: a device that reads everything in one sweep stamps the payload and leaves
        // the metrics bare.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Datatype = (uint)DataType.Float,
            FloatValue = 3.6875f,
        };

        SparkplugPayload.DecodeData(SparkplugPayloads.Encode(PayloadMs, seq: 1, metric), MetricAliasTable.Empty)
            .Single()
            .DeviceTimestamp
            .ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds((long)PayloadMs));
    }

    [Fact]
    public void MetricsKeepTheirOwnInstantsRatherThanThePayloadsOne()
    {
        // One message routinely gathers readings taken at different moments — that is why a Sparkplug
        // metric has a timestamp of its own at all. Collapsing them onto the payload's would align
        // samples that were never simultaneous, and process engineering reads exactly those gaps.
        var readings = SparkplugPayload.DecodeData(
            SparkplugPayloads.Encode(
                PayloadMs,
                seq: 1,
                Stamped("Formation/Voltage", PayloadMs - 400),
                Stamped("Formation/Temperature", PayloadMs - 150)),
            MetricAliasTable.Empty);

        readings.Select(reading => reading.DeviceTimestamp).ShouldBe(
        [
            DateTimeOffset.FromUnixTimeMilliseconds((long)PayloadMs - 400),
            DateTimeOffset.FromUnixTimeMilliseconds((long)PayloadMs - 150),
        ]);
    }

    [Fact]
    public void AReadingWithNoTimestampAnywhereIsRefused()
    {
        // device_timestamp is part of the natural key (docs/scope.md §7.2), so a reading without one
        // has no dedup identity. Accepting it would mean writing a measurement that could never be
        // recognised as a duplicate of itself — and C04 would have no way to notice.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Datatype = (uint)DataType.Float,
            FloatValue = 3.6875f,
        };

        Should.Throw<SparkplugDecodeException>(() => SparkplugPayload.DecodeData(
            SparkplugPayloads.Encode(timestamp: null, seq: 1, metric),
            MetricAliasTable.Empty));
    }

    [Fact]
    public void AClockSoWrongItIsNotAnInstantIsRefusedAsABadMessage()
    {
        // A PLC with a corrupted clock must not take ingestion down with an ArgumentOutOfRangeException
        // thrown from somewhere in the BCL. It is a bad message and it gets a bad message's answer.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Datatype = (uint)DataType.Float,
            Timestamp = ulong.MaxValue,
            FloatValue = 3.6875f,
        };

        Should.Throw<SparkplugDecodeException>(() =>
            SparkplugPayload.DecodeData(SparkplugPayloads.Carrying(metric), MetricAliasTable.Empty));
    }

    private static SparkplugMetric Stamped(string name, ulong milliseconds) =>
        new()
        {
            Name = name,
            Datatype = (uint)DataType.Float,
            Timestamp = milliseconds,
            FloatValue = 1f,
        };

    private static IEnumerable<string> MembersMentioningDateTime(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => Mentions(property.PropertyType))
            .Select(property => $"{type.Name}.{property.Name}");

    private static bool Mentions(Type type) =>
        type == typeof(DateTime)
        || (type.IsArray && Mentions(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(Mentions));

    /// <summary>A deliberately wrong reading, so the walk above can be shown to be able to fail.</summary>
    public sealed record ReadingWithForbiddenClock(
        string MetricName,
        DateTimeOffset DeviceTimestamp,
        IReadOnlyList<DateTime> Samples);
}
