using Nvm.Sparkplug;
using Org.Eclipse.Tahu.Protobuf;
using SparkplugMetric = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>What a birth is required to say, and what happens when it does not say it.</summary>
/// <remarks>
/// Strict on purpose. A birth is the only statement of what every later message of the session means,
/// and it arrives once; a birth accepted with a hole in it turns into hours of readings that cannot be
/// interpreted, discovered long after the node has moved on.
/// </remarks>
public sealed class SparkplugBirthTests
{
    [Fact]
    public void ABirthMetricWithoutANameIsRefused()
    {
        // An alias-only metric is normal in a DDATA and meaningless in a birth: the birth is where the
        // number is supposed to acquire a meaning.
        var metric = new SparkplugMetric
        {
            Alias = 1,
            Datatype = (uint)DataType.Float,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 3.6875f,
        };

        Should.Throw<SparkplugDecodeException>(() =>
            SparkplugPayload.DecodeBirth(SparkplugPayloads.Carrying(metric)));
    }

    [Fact]
    public void ABirthMetricWithoutADatatypeIsRefused()
    {
        // The value could be inferred from the field it arrived in, and for a float it would even be
        // right. It is refused anyway because of the integers: int_value carries both Int32 and
        // UInt32, so a birth that does not declare leaves -1 and 4294967295 indistinguishable for
        // every message of the rest of the session.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Alias = 1,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 3.6875f,
        };

        var thrown = Should.Throw<SparkplugDecodeException>(() =>
            SparkplugPayload.DecodeBirth(SparkplugPayloads.Carrying(metric)));

        thrown.Message.ShouldContain("Formation/Voltage");
    }

    [Fact]
    public void ABirthThatGivesOneAliasToTwoMetricsIsRefused()
    {
        // Whichever of the two was read last would win, and every later message using that alias would
        // be filed under it. Refusing the birth costs one rebirth; accepting it costs a session of
        // readings attributed to the wrong signal.
        var payload = SparkplugPayloads.BirthDeclaring(
            ("Formation/Voltage", 1),
            ("Formation/Temperature", 1));

        var thrown = Should.Throw<SparkplugDecodeException>(() => SparkplugPayload.DecodeBirth(payload));

        thrown.Message.ShouldContain("Formation/Voltage");
        thrown.Message.ShouldContain("Formation/Temperature");
    }

    [Fact]
    public void ABirthThatDeclaresOneMetricTwiceIsRefused()
    {
        var payload = SparkplugPayloads.BirthDeclaring(
            ("Formation/Voltage", 1),
            ("Formation/Voltage", 2));

        Should.Throw<SparkplugDecodeException>(() => SparkplugPayload.DecodeBirth(payload));
    }

    [Fact]
    public void AMetricWithNeitherANameNorAnAliasIsRefused()
    {
        // Nothing to attribute the value to. Sparkplug allows it on the wire; there is no reading it.
        var metric = new SparkplugMetric
        {
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 3.6875f,
        };

        Should.Throw<SparkplugDecodeException>(() =>
            SparkplugPayload.DecodeData(SparkplugPayloads.Carrying(metric), MetricAliasTable.Empty));
    }

    [Fact]
    public void ANamedMetricIsAcceptedMidSessionWithoutAnAliasTable()
    {
        // The other half of the alias rule, and the one that keeps it from being a blanket refusal:
        // a metric that names itself needs no table, because it is not asking anyone to remember
        // anything.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Datatype = (uint)DataType.Float,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 3.6875f,
        };

        var readings = SparkplugPayload.DecodeData(
            SparkplugPayloads.Carrying(metric),
            MetricAliasTable.Empty);

        readings.Single().MetricName.ShouldBe("Formation/Voltage");
        readings.Single().Alias.ShouldBeNull();
    }

    [Fact]
    public void AnAliasThatArrivesUnderADifferentNameThanTheBirthGaveItIsRefused()
    {
        // This one reading could have been filed correctly — it named itself. The next message under
        // alias 1 could not: it would carry no name, and the stale table would send it to
        // Formation/Voltage. Accepting the contradiction here buys one correct reading and pays for it
        // with every alias-only message that follows.
        var birth = SparkplugPayload.DecodeBirth(SparkplugPayloads.BirthDeclaring(("Formation/Voltage", 1)));

        var renamed = new SparkplugMetric
        {
            Name = "Formation/Temperature",
            Alias = 1,
            Datatype = (uint)DataType.Float,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 31.5f,
        };

        var thrown = Should.Throw<UnknownMetricAliasException>(() =>
            SparkplugPayload.DecodeData(SparkplugPayloads.Carrying(renamed), birth.Aliases));

        thrown.Message.ShouldContain("Formation/Voltage");
        thrown.Message.ShouldContain("Formation/Temperature");
    }

    [Fact]
    public void ABirthWithNoAliasesAtAllProducesAnEmptyTableRatherThanFailing()
    {
        // Aliases are an optimisation, not a requirement. A device that spells every name out in full
        // is wasteful and perfectly legal, and its birth still has to decode.
        var metric = new SparkplugMetric
        {
            Name = "Formation/Voltage",
            Datatype = (uint)DataType.Float,
            Timestamp = SparkplugPayloads.DefaultTimestampMs,
            FloatValue = 3.6875f,
        };

        var birth = SparkplugPayload.DecodeBirth(SparkplugPayloads.Carrying(metric));

        birth.Readings.Length.ShouldBe(1);
        birth.Readings[0].Alias.ShouldBeNull();
        birth.Aliases.Count.ShouldBe(0);
        birth.Aliases.ShouldBeSameAs(MetricAliasTable.Empty);
    }
}
