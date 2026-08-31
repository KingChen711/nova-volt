using System.Collections.Immutable;
using Nvm.Kernel.Identity;

namespace Nvm.TelemetryBackfill;

/// <summary>The physical process interval from which the generator emits readings.</summary>
public sealed record TelemetryBackfillSpec
{
    /// <summary>Creates one half-open historical interval.</summary>
    public TelemetryBackfillSpec(
        EquipmentPath linePath,
        ImmutableArray<EquipmentPath> channels,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        TimeSpan samplePeriod,
        double driftedDeviceRate,
        TimeSpan clockDrift,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(linePath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(samplePeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(driftedDeviceRate, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(driftedDeviceRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(clockDrift, TimeSpan.Zero);

        if (channels.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one formation channel is required.", nameof(channels));
        }

        if (endAt <= startAt)
        {
            throw new ArgumentException("The backfill interval must have a positive duration.", nameof(endAt));
        }

        if ((endAt - startAt).Ticks % samplePeriod.Ticks != 0)
        {
            throw new ArgumentException("The backfill interval must contain a whole number of samples.", nameof(samplePeriod));
        }

        LinePath = linePath;
        Channels = channels;
        StartAt = startAt.ToUniversalTime();
        EndAt = endAt.ToUniversalTime();
        SamplePeriod = samplePeriod;
        DriftedDeviceRate = driftedDeviceRate;
        ClockDrift = clockDrift;
        RecordedAt = recordedAt.ToUniversalTime();
    }

    /// <summary>The line whose formation model is reused.</summary>
    public EquipmentPath LinePath { get; }

    /// <summary>Channels in stable factory-model order.</summary>
    public ImmutableArray<EquipmentPath> Channels { get; }

    /// <summary>Inclusive process-time boundary.</summary>
    public DateTimeOffset StartAt { get; }

    /// <summary>Exclusive process-time boundary.</summary>
    public DateTimeOffset EndAt { get; }

    /// <summary>How far process time advances per sample.</summary>
    public TimeSpan SamplePeriod { get; }

    /// <summary>Share of channels whose front-panel clock is wrong.</summary>
    public double DriftedDeviceRate { get; }

    /// <summary>Absolute clock error applied to those channels.</summary>
    public TimeSpan ClockDrift { get; }

    /// <summary>When this backfill run is recorded.</summary>
    public DateTimeOffset RecordedAt { get; }
}
