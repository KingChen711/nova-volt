namespace Nvm.Simulator.Faults;

/// <summary>Which devices have a wrong clock, and by how much.</summary>
/// <remarks>
/// <para>
/// A PLC clock is wrong for reasons that do not go away between messages: a flat CMOS battery, no
/// route from the OT floor to an NTP server, a board swapped in with the factory default on it. So
/// the choice is made <b>per device and once</b>, from the device code, and stays put for the life of
/// the plant — the same channel is the broken one this morning and this afternoon.
/// </para>
/// <para>
/// Only <c>device_timestamp</c> moves. The reading itself, the cell serial, the sequence number and
/// the topic are all correct, because they are correct on the real thing too: the cell in the channel
/// does not change identity when the clock on the front panel is wrong. That is the whole difficulty
/// of the case — nothing about the message looks broken, and the only way to know is to compare it
/// against a clock you trust, which is what the gateway timestamp is for (C13).
/// </para>
/// </remarks>
public sealed class DeviceClockDrift
{
    /// <summary>Every clock correct, which is what a plant with no fault injected looks like.</summary>
    public static DeviceClockDrift None { get; } = new(0, TimeSpan.Zero);

    private const uint Buckets = 10_000;

    private readonly uint _threshold;

    /// <summary>Creates the fault.</summary>
    /// <param name="driftedDeviceRate">Share of devices whose clock is wrong, 0 to 1.</param>
    /// <param name="magnitude">How far wrong. Applied as plus on some devices and minus on others.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not a share.</exception>
    public DeviceClockDrift(double driftedDeviceRate, TimeSpan magnitude)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(driftedDeviceRate, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(driftedDeviceRate, 1);

        DriftedDeviceRate = driftedDeviceRate;
        Magnitude = magnitude < TimeSpan.Zero ? magnitude.Negate() : magnitude;
        _threshold = (uint)Math.Round(driftedDeviceRate * Buckets);
    }

    /// <summary>Share of devices whose clock is wrong.</summary>
    public double DriftedDeviceRate { get; }

    /// <summary>How far a wrong clock is wrong.</summary>
    public TimeSpan Magnitude { get; }

    /// <summary>How far this device's clock is out. Zero for a device whose clock is right.</summary>
    /// <param name="deviceCode">The device, for example <c>FORM-01-CH-0142</c>.</param>
    public TimeSpan For(string deviceCode)
    {
        ArgumentNullException.ThrowIfNull(deviceCode);

        if (_threshold == 0 || Magnitude == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var hash = StableHash.Of(deviceCode);

        if (hash % Buckets >= _threshold)
        {
            return TimeSpan.Zero;
        }

        // A different slice of the same hash decides the sign, so which devices are wrong and which
        // way they are wrong are independent. Taking both from the low bits would make every drifted
        // clock lean the same way, and a test that only ever saw a late clock would not notice code
        // that assumed the device is never ahead of the gateway.
        return (hash >> 20 & 1) == 0 ? Magnitude.Negate() : Magnitude;
    }
}
