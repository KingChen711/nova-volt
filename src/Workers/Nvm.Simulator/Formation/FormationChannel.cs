using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.Simulator.Formation;

/// <summary>One charging channel of a formation cycler, with a cell in it.</summary>
/// <remarks>
/// <para>
/// Holds the two pieces of state a real channel has: which cell is loaded, and what it last told
/// anybody. The second is what makes report-by-exception possible — a reading is only sent when it
/// has moved further than the deadband, which is why an idle channel is silent and a channel in the
/// CV tail is nearly silent too.
/// </para>
/// <para>
/// The channel produces readings, not messages. Assembling a Sparkplug message needs the edge node's
/// sequence number, and that belongs to the node rather than to any one device under it — see
/// <see cref="FormationLine"/>.
/// </para>
/// </remarks>
public sealed class FormationChannel
{
    /// <summary>Metric names and the aliases the birth gives them.</summary>
    private const string VoltageMetric = "Formation/Voltage";
    private const string CurrentMetric = "Formation/Current";
    private const string TemperatureMetric = "Formation/Temperature";
    private const string CapacityMetric = "Formation/Capacity";
    private const string StepMetric = "Formation/StepIndex";
    private const string CellSerialMetric = "Formation/CellSerial";

    private const ulong VoltageAlias = 1;
    private const ulong CurrentAlias = 2;
    private const ulong TemperatureAlias = 3;
    private const ulong CapacityAlias = 4;
    private const ulong StepAlias = 5;
    private const ulong CellSerialAlias = 6;

    // Deadbands, in the unit of each signal. Chosen so that the CC leg reports steadily and the rests
    // report almost nothing, which is the traffic shape a real line has: quiet channels are the norm.
    private const double VoltageDeadband = 0.001;
    private const double CurrentDeadband = 0.001;
    private const double TemperatureDeadband = 0.05;
    private const double CapacityDeadband = 0.001;

    private readonly FormationProfile _profile;
    private readonly double _spread;

    private double _lastVolts;
    private double _lastAmperes;
    private double _lastCelsius;
    private double _lastAmpHours;
    private FormationStep _lastStep;

    /// <summary>Creates a channel that is empty until a cell is loaded into it.</summary>
    /// <param name="path">Where the channel is, for example <c>…/FORM-01/FORM-01-CH-0001</c>.</param>
    /// <param name="profile">The cycle shape every cell in this channel follows.</param>
    public FormationChannel(EquipmentPath path, FormationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(profile);

        Path = path;
        _profile = profile;
        _spread = SpreadOf(path.Code);
    }

    /// <summary>Where this channel is.</summary>
    public EquipmentPath Path { get; }

    /// <summary>The cell currently in the channel, or null when it is empty.</summary>
    public string? CellSerial { get; private set; }

    /// <summary>How many measurements this channel has emitted since it was created.</summary>
    /// <remarks>
    /// The left-hand side of the reconciliation in D1. Counts <b>signals</b>, so the cell serial is
    /// not in it: the serial says which unit the readings are about, and is an association rather
    /// than something measured.
    /// </remarks>
    public long MeasurementCount { get; private set; }

    /// <summary>Declares the channel: every metric, by name, alias and current value.</summary>
    /// <param name="cellSerial">The cell in the channel.</param>
    /// <param name="elapsed">How far into its cycle that cell is.</param>
    /// <param name="at">The device clock for this declaration.</param>
    /// <remarks>
    /// Covers both occasions a <c>DBIRTH</c> is published, because they are the same message: a new
    /// cell going in, and the node reconnecting and having to restate everything. The elapsed time is
    /// a parameter for exactly that reason — a reconnect happens wherever the cycle happens to be.
    /// It also resets the deadband state, so the first reading after a birth is always sent.
    /// </remarks>
    public ImmutableArray<DeviceReading> Declare(string cellSerial, TimeSpan elapsed, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cellSerial);

        CellSerial = cellSerial;

        var sample = Shift(_profile.At(elapsed));

        Remember(sample);
        MeasurementCount += 5;

        return
        [
            Reading(VoltageMetric, VoltageAlias, new MetricValue.Real(sample.Volts), at),
            Reading(CurrentMetric, CurrentAlias, new MetricValue.Real(sample.Amperes), at),
            Reading(TemperatureMetric, TemperatureAlias, new MetricValue.Real(sample.Celsius), at),
            Reading(CapacityMetric, CapacityAlias, new MetricValue.Real(sample.AmpHours), at),
            Reading(StepMetric, StepAlias, new MetricValue.Integral((long)sample.Step), at),
            Reading(CellSerialMetric, CellSerialAlias, new MetricValue.Text(cellSerial), at),
        ];
    }

    /// <summary>Reads the channel and returns only what has moved since the last time.</summary>
    /// <param name="elapsed">How far into the cycle the cell is.</param>
    /// <param name="at">The device clock for this sample.</param>
    /// <returns>The changed readings, which is often none at all.</returns>
    /// <exception cref="InvalidOperationException">The channel is empty.</exception>
    public ImmutableArray<DeviceReading> Sample(TimeSpan elapsed, DateTimeOffset at)
    {
        if (CellSerial is null)
        {
            throw new InvalidOperationException(
                $"Channel '{Path.Value}' has no cell in it, so there is nothing to measure.");
        }

        var sample = Shift(_profile.At(elapsed));
        var changed = ImmutableArray.CreateBuilder<DeviceReading>(5);

        if (Math.Abs(sample.Volts - _lastVolts) >= VoltageDeadband)
        {
            changed.Add(Reading(VoltageMetric, VoltageAlias, new MetricValue.Real(sample.Volts), at));
        }

        if (Math.Abs(sample.Amperes - _lastAmperes) >= CurrentDeadband)
        {
            changed.Add(Reading(CurrentMetric, CurrentAlias, new MetricValue.Real(sample.Amperes), at));
        }

        if (Math.Abs(sample.Celsius - _lastCelsius) >= TemperatureDeadband)
        {
            changed.Add(Reading(TemperatureMetric, TemperatureAlias, new MetricValue.Real(sample.Celsius), at));
        }

        if (Math.Abs(sample.AmpHours - _lastAmpHours) >= CapacityDeadband)
        {
            changed.Add(Reading(CapacityMetric, CapacityAlias, new MetricValue.Real(sample.AmpHours), at));
        }

        if (sample.Step != _lastStep)
        {
            changed.Add(Reading(StepMetric, StepAlias, new MetricValue.Integral((long)sample.Step), at));
        }

        // Only the signals that were actually sent are remembered. Remembering the sample instead
        // would let a value creep past the deadband one sub-threshold step at a time and never be
        // reported — the classic report-by-exception bug, where a slow drift is invisible.
        RememberSent(changed, sample);

        MeasurementCount += changed.Count;

        return changed.DrainToImmutable();
    }

    private static DeviceReading Reading(string name, ulong alias, MetricValue value, DateTimeOffset at) =>
        new(name, alias, value, at);

    /// <summary>A stable per-channel offset in [-1, 1].</summary>
    /// <remarks>
    /// FNV-1a over the channel code rather than <see cref="string.GetHashCode()"/>, which is
    /// randomised per process: a simulator whose channels swapped personalities on every restart would
    /// make every comparison between two runs meaningless.
    /// </remarks>
    private static double SpreadOf(string code)
    {
        var hash = 2166136261u;

        foreach (var character in code)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return ((hash % 2001) / 1000.0) - 1.0;
    }

    // Two cells are never identical, and a line where every channel reads exactly the same number is a
    // line where a grouping bug downstream is invisible. The spread is small enough to stay inside
    // what a real cycler would pass and large enough to tell channels apart.
    private FormationSample Shift(FormationSample sample) =>
        sample with
        {
            Volts = sample.Volts * (1 + (_spread * 0.005)),
            Celsius = sample.Celsius + (_spread * 0.8),
            AmpHours = sample.AmpHours * (1 + (_spread * 0.01)),
        };

    private void Remember(FormationSample sample)
    {
        _lastVolts = sample.Volts;
        _lastAmperes = sample.Amperes;
        _lastCelsius = sample.Celsius;
        _lastAmpHours = sample.AmpHours;
        _lastStep = sample.Step;
    }

    private void RememberSent(IEnumerable<DeviceReading> sent, FormationSample sample)
    {
        foreach (var reading in sent)
        {
            switch (reading.Alias)
            {
                case VoltageAlias:
                    _lastVolts = sample.Volts;
                    break;
                case CurrentAlias:
                    _lastAmperes = sample.Amperes;
                    break;
                case TemperatureAlias:
                    _lastCelsius = sample.Celsius;
                    break;
                case CapacityAlias:
                    _lastAmpHours = sample.AmpHours;
                    break;
                case StepAlias:
                    _lastStep = sample.Step;
                    break;
                default:
                    break;
            }
        }
    }
}
