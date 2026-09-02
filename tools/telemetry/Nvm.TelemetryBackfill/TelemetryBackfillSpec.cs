using System.Collections.Immutable;
using Nvm.Kernel.Identity;

namespace Nvm.TelemetryBackfill;

/// <summary>Khoảng process vật lý mà generator emit reading từ đó.</summary>
public sealed record TelemetryBackfillSpec
{
    /// <summary>Tạo một khoảng thời gian lịch sử half-open.</summary>
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

    /// <summary>Line có formation model được tái sử dụng.</summary>
    public EquipmentPath LinePath { get; }

    /// <summary>Channel theo thứ tự factory-model ổn định.</summary>
    public ImmutableArray<EquipmentPath> Channels { get; }

    /// <summary>Ranh giới process-time bao gồm cả điểm đầu.</summary>
    public DateTimeOffset StartAt { get; }

    /// <summary>Ranh giới process-time không bao gồm điểm cuối.</summary>
    public DateTimeOffset EndAt { get; }

    /// <summary>Process time tiến lên bao xa cho mỗi sample.</summary>
    public TimeSpan SamplePeriod { get; }

    /// <summary>Tỷ lệ channel có đồng hồ front-panel bị sai.</summary>
    public double DriftedDeviceRate { get; }

    /// <summary>Sai số đồng hồ tuyệt đối áp dụng cho các channel đó.</summary>
    public TimeSpan ClockDrift { get; }

    /// <summary>Thời điểm lần chạy backfill này được ghi nhận.</summary>
    public DateTimeOffset RecordedAt { get; }
}
