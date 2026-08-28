using Nvm.Kernel.Identity;

namespace Nvm.Sparkplug;

/// <summary>Gives a decoded reading the identity the rest of the system deduplicates on.</summary>
/// <remarks>
/// The join between what came off the wire and <see cref="MeasurementNaturalKey"/>. It sits here
/// rather than in <c>Nvm.Kernel</c> because the key is shared and the Sparkplug reading is not — C15
/// builds the same key from a CSV row, and the two must land on the same value for the same reading
/// or a batch delivered twice by two routes is stored twice.
/// </remarks>
public static class DeviceReadingIdentity
{
    /// <summary>Derives the natural key of a reading taken at a known place in the plant.</summary>
    /// <param name="reading">The decoded reading.</param>
    /// <param name="equipmentPath">Where it was taken — the resolved path, not the topic.</param>
    /// <param name="unitId">The unit under the machine, when one is known.</param>
    /// <exception cref="ArgumentException">
    /// The path names no plant, or is shallower than a work cell and so performs no step.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The <b>resolved</b> path, deliberately. A topic carries a device code and no work cell, so
    /// keying on anything the topic can produce on its own would give
    /// <c>NOVAVOLT/NV1/FORMATION/F1/FORM-01-CH-0142</c> — a place that does not exist — and the key
    /// would change the day somebody fixed it.
    /// </para>
    /// <para>
    /// The signal code is the metric name as the device declared it. Not normalised, not
    /// upper-cased: it is the plant's own vocabulary, it goes into
    /// <c>ts.process_signal.signal_code</c> as it stands, and a normalisation applied here and not
    /// there would make the key and the stored row disagree about what was measured.
    /// </para>
    /// </remarks>
    public static MeasurementNaturalKey NaturalKey(
        this DeviceReading reading,
        EquipmentPath equipmentPath,
        string? unitId = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(equipmentPath);

        var stepCode = ProcessStepCode.FromEquipmentPath(equipmentPath)
            ?? throw new ArgumentException(
                $"'{equipmentPath.Value}' is a {equipmentPath.Kind} and performs no process step, so "
                + "a measurement cannot be attributed to it.",
                nameof(equipmentPath));

        return MeasurementNaturalKey.For(
            equipmentPath,
            stepCode,
            reading.MetricName,
            reading.DeviceTimestamp,
            unitId);
    }
}
