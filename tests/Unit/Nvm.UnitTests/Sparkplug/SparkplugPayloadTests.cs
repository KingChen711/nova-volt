using Org.Eclipse.Tahu.Protobuf;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Decodes real Sparkplug B bytes with the code generated from the vendored schema.</summary>
/// <remarks>
/// <para>
/// The payloads were produced by <c>pysparkplug</c>, which carries its own copy of the Sparkplug
/// schema — see <c>tests/Fixtures/sparkplug/README.md</c>. That is the whole point of them. Encoding
/// with our own generated code and decoding it again passes just as happily when both directions are
/// wrong, and a schema is exactly the kind of thing that is wrong in both directions at once.
/// </para>
/// <para>
/// The pair is a DBIRTH and the DDATA that follows it, because on a real line neither one means
/// anything alone. That relationship is what C11 turns into node state; here it is only decoded.
/// </para>
/// </remarks>
public sealed class SparkplugPayloadTests
{
    /// <summary>2026-08-28T07:15:30.500Z. Sparkplug timestamps are milliseconds since the Unix epoch, UTC.</summary>
    private const ulong BirthTimestampMs = 1787901330500;

    /// <summary>Two seconds later — the report-by-exception update.</summary>
    private const ulong DataTimestampMs = 1787901332500;

    [Fact]
    public void ADeviceBirthDeclaresEveryMetricByNameAliasAndType()
    {
        var birth = Payload.Parser.ParseFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        birth.Timestamp.ShouldBe(BirthTimestampMs);
        birth.Seq.ShouldBe(1UL);
        birth.Metrics.Count.ShouldBe(5);

        birth.Metrics.Select(metric => metric.Name).ShouldBe(
        [
            "Formation/Voltage",
            "Formation/Current",
            "Formation/Temperature",
            "Formation/StepIndex",
            "Formation/CellSerial",
        ]);

        // Aliases are assigned here and nowhere else. Everything after this message refers to a
        // measurement by its number.
        birth.Metrics.Select(metric => metric.Alias).ShouldBe([1UL, 2UL, 3UL, 4UL, 5UL]);

        // Exact equality, not a tolerance: the fixture uses values binary32 represents exactly, so a
        // decoder that read four bytes from the wrong offset lands somewhere obviously different
        // rather than somewhere close enough to pass.
        var voltage = birth.Metrics[0];
        voltage.Datatype.ShouldBe((uint)DataType.Float);
        voltage.ValueCase.ShouldBe(Payload.Types.Metric.ValueOneofCase.FloatValue);
        voltage.FloatValue.ShouldBe(3.6875f);

        // Three datatypes in one payload, so the oneof is actually exercised. A decoder that always
        // reads float_value passes a payload made only of floats.
        birth.Metrics[3].Datatype.ShouldBe((uint)DataType.Int32);
        birth.Metrics[3].IntValue.ShouldBe(2U);

        birth.Metrics[4].Datatype.ShouldBe((uint)DataType.String);
        birth.Metrics[4].StringValue.ShouldBe("NV1CL16238A00123");
    }

    [Fact]
    public void AReportByExceptionUpdateCarriesAliasesAndNothingElse()
    {
        var data = Payload.Parser.ParseFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));

        data.Timestamp.ShouldBe(DataTimestampMs);
        data.Seq.ShouldBe(2UL);

        // Two of the five, because only two values moved. "Nothing arrived" on this device therefore
        // means "nothing changed" just as often as it means "the link is down", and only NDEATH tells
        // the two apart — see the report-by-exception entry in docs/glossary.md.
        data.Metrics.Count.ShouldBe(2);

        foreach (var metric in data.Metrics)
        {
            metric.HasName.ShouldBeFalse("a DDATA carries no names — that is what the aliases are for");
            metric.HasDatatype.ShouldBeFalse("the datatype was declared at birth and is not repeated");
            metric.HasAlias.ShouldBeTrue();
        }

        data.Metrics.Select(metric => metric.Alias).ShouldBe([1UL, 3UL]);
        data.Metrics.Select(metric => metric.FloatValue).ShouldBe([3.71875f, 31.75f]);
    }

    [Fact]
    public void OnlyTheBirthMakesTheUpdateReadable()
    {
        // The join C11 will have to keep in memory per edge node, done here by hand so the cost of
        // losing it is visible: without the birth, alias 1 is a number with a float attached to it,
        // and no amount of retrying the DDATA recovers what it measured.
        var birth = Payload.Parser.ParseFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));
        var data = Payload.Parser.ParseFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));

        var names = birth.Metrics.ToDictionary(metric => metric.Alias, metric => metric.Name);

        var readings = data.Metrics.ToDictionary(metric => names[metric.Alias], metric => metric.FloatValue);

        readings.ShouldBe(new Dictionary<string, float>
        {
            ["Formation/Voltage"] = 3.71875f,
            ["Formation/Temperature"] = 31.75f,
        });
    }
}
