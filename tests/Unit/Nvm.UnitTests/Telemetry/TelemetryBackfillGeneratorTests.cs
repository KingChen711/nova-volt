using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.TelemetryBackfill;

namespace Nvm.UnitTests.Telemetry;

public sealed class TelemetryBackfillGeneratorTests
{
    private static readonly EquipmentPath Line =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");

    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    [Fact]
    public void Generator_ReplaysFormationLineReportByExceptionAndTheSharedNaturalKey()
    {
        var start = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var spec = new TelemetryBackfillSpec(
            Line,
            ImmutableArray.Create(Channel),
            start,
            start.AddHours(1),
            TimeSpan.FromMinutes(1),
            driftedDeviceRate: 0,
            clockDrift: TimeSpan.FromHours(2),
            recordedAt: start.AddDays(2));
        var first = TelemetryBackfillGenerator.Generate(spec).ToArray();
        var replay = TelemetryBackfillGenerator.Generate(spec).ToArray();

        replay.ShouldBe(first);
        first.ShouldAllBe(row =>
            row.SourceEventId == MeasurementNaturalKey.For(
                EquipmentPath.Parse(row.EquipmentId),
                row.StepCode,
                row.SignalCode,
                row.DeviceTimestamp,
                row.UnitId).SourceEventId.Value);

        var voltage = first.Count(row => row.SignalCode == "Formation/Voltage");
        var temperature = first.Count(row => row.SignalCode == "Formation/Temperature");

        voltage.ShouldBeGreaterThan(temperature);
        first.Where(row => row.SignalCode == "Formation/Voltage")
            .ShouldAllBe(row =>
                row.RealValue.HasValue
                && row.RealValue.GetValueOrDefault() >= 2.9
                && row.RealValue.GetValueOrDefault() <= 4.3);
        first.Where(row => row.SignalCode == "Formation/Temperature")
            .ShouldAllBe(row =>
                row.RealValue.HasValue
                && row.RealValue.GetValueOrDefault() >= 24
                && row.RealValue.GetValueOrDefault() <= 33);
    }

    [Fact]
    public void Topology_TakesOneHundredChannelsPerCyclerInsteadOfInventingOneHugeMachine()
    {
        var channels = BackfillTopology.Load(
            Path.Combine(AppContext.BaseDirectory, "seed-load"),
            Line,
            channelCount: 101);

        channels.Take(100)
            .ShouldAllBe(path => path.Value.Contains("/FORM-01/", StringComparison.Ordinal));
        channels[100].Value.ShouldContain("/FORM-02/");
    }
}
