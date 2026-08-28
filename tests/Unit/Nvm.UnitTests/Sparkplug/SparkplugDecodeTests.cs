using Nvm.Sparkplug;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Decodes the real captured payloads into readings.</summary>
/// <remarks>
/// The bytes come from <c>tests/Fixtures/sparkplug/</c> and were produced by a different Sparkplug
/// implementation, so these tests check the decoder against the specification rather than against
/// itself. <c>SparkplugValueTests</c> builds its payloads in-process instead, which is fine for the
/// question it asks — which datatype maps to which case — because the schema is already pinned here
/// and by <c>SparkplugPinTests</c>.
/// </remarks>
public sealed class SparkplugDecodeTests
{
    /// <summary>2026-08-28T07:15:30.500Z, the instant the fixture birth is stamped with.</summary>
    private static readonly DateTimeOffset BirthInstant =
        DateTimeOffset.FromUnixTimeMilliseconds(1787901330500);

    /// <summary>Two seconds later — the report-by-exception update.</summary>
    private static readonly DateTimeOffset DataInstant =
        DateTimeOffset.FromUnixTimeMilliseconds(1787901332500);

    [Fact]
    public void ABirthDeclaresEveryMetricAndBuildsTheAliasTable()
    {
        var birth = SparkplugPayload.DecodeBirth(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        birth.Readings.Select(reading => reading.MetricName).ShouldBe(
        [
            "Formation/Voltage",
            "Formation/Current",
            "Formation/Temperature",
            "Formation/StepIndex",
            "Formation/CellSerial",
        ]);

        birth.Readings.Select(reading => reading.Alias).ShouldBe([1UL, 2UL, 3UL, 4UL, 5UL]);

        // The birth reports current values as well as declaring names. That is what lets a listener
        // which has just connected show a full picture instead of a blank one until every metric
        // happens to change.
        birth.Readings[0].Value.ShouldBe(new MetricValue.Real(3.6875));
        birth.Readings[3].Value.ShouldBe(new MetricValue.Integral(2));
        birth.Readings[4].Value.ShouldBe(new MetricValue.Text("NV1CL16238A00123"));

        birth.Aliases.Count.ShouldBe(5);
        birth.Aliases.Aliases.ShouldBe([1UL, 2UL, 3UL, 4UL, 5UL]);
        birth.Aliases.TryGetMetricName(3, out var name).ShouldBeTrue();
        name.ShouldBe("Formation/Temperature");
    }

    [Fact]
    public void AnUpdateResolvesItsAliasesThroughTheBirth()
    {
        var birth = SparkplugPayload.DecodeBirth(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        var readings = SparkplugPayload.DecodeData(
            SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData),
            birth.Aliases);

        // Two of the five, because only two values moved. The payload itself carries neither name nor
        // datatype for them — everything below is recovered from the birth.
        readings.Length.ShouldBe(2);

        readings.Select(reading => reading.MetricName)
            .ShouldBe(["Formation/Voltage", "Formation/Temperature"]);

        readings.Select(reading => reading.Value)
            .ShouldBe([new MetricValue.Real(3.71875), new MetricValue.Real(31.75)]);
    }

    [Fact]
    public void TheSameUpdateWithoutABirthThrowsRatherThanReturningNothing()
    {
        // The failure this test exists for is not the exception; it is the alternative. A decoder that
        // skipped the aliases it could not resolve would return an empty array here, and an empty
        // array is exactly what report-by-exception produces when nothing changed. Ingestion would
        // record a healthy channel with no readings, and the gap would look like a quiet machine.
        var thrown = Should.Throw<UnknownMetricAliasException>(() => SparkplugPayload.DecodeData(
            SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData),
            MetricAliasTable.Empty));

        thrown.Alias.ShouldBe(1UL);
        thrown.KnownAliasCount.ShouldBe(0);
    }

    [Fact]
    public void AnAliasTableFromTheWrongSessionIsNotSilentlyAcceptedEither()
    {
        // A node that reconnects renumbers freely. Here the table knows alias 1 but not alias 3, which
        // is the shape of a partially stale table — and the half it does resolve is the dangerous
        // half, because it makes the result look plausible.
        var partial = SparkplugPayload
            .DecodeBirth(SparkplugPayloads.BirthDeclaring(("Formation/Voltage", 1)))
            .Aliases;

        var thrown = Should.Throw<UnknownMetricAliasException>(() => SparkplugPayload.DecodeData(
            SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData),
            partial));

        thrown.Alias.ShouldBe(3UL);
        thrown.KnownAliasCount.ShouldBe(1);
    }

    [Fact]
    public void EveryReadingCarriesTheDeviceClock()
    {
        var birth = SparkplugPayload.DecodeBirth(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        var readings = SparkplugPayload.DecodeData(
            SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData),
            birth.Aliases);

        birth.Readings.ShouldAllBe(reading => reading.DeviceTimestamp == BirthInstant);
        readings.ShouldAllBe(reading => reading.DeviceTimestamp == DataInstant);

        // Not the moment of decoding, and not the gateway's clock. docs/scope.md §7.3 keeps the three
        // apart because the device's is the one that is routinely hours wrong, and C13 has to be able
        // to say so rather than having it quietly replaced here.
        readings[0].DeviceTimestamp.ShouldBe(DataInstant);
        readings[0].DeviceTimestamp.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void ATruncatedPayloadIsRefused()
    {
        // Not hypothetical: C09 buffers payloads on disk, and a record cut short by a power cut is the
        // failure that buffer is designed around. It has to arrive here as a decode error rather than
        // as a payload with fewer metrics than were written.
        var complete = SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth);
        var truncated = complete[..^5];

        Should.Throw<SparkplugDecodeException>(() => SparkplugPayload.DecodeBirth(truncated));
    }
}
