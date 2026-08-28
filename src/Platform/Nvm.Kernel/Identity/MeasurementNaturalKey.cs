using System.Globalization;
using Nvm.Kernel.Commands;

namespace Nvm.Kernel.Identity;

/// <summary>What makes one measurement one measurement, and the identity derived from it.</summary>
/// <remarks>
/// <para>
/// The six fields of docs/scope.md §7.2:
/// <c>(site_id, equipment_id, unit_id, step_code, device_timestamp, signal_code)</c>. They are the
/// fields the fact already has — nothing is generated, so the same reading arriving twice, from a
/// device that got no acknowledgement and from a gateway flushing its backlog three hours later,
/// produces the same <see cref="SourceEventId"/> on any machine in any process.
/// </para>
/// <para>
/// Shared rather than Sparkplug-specific. The CSV file drop in C15 has to land on the same key for
/// the same reading, or a plant sending its morning batch twice — once over MQTT and once as a file
/// somebody re-uploaded — would store it twice.
/// </para>
/// </remarks>
public sealed record MeasurementNaturalKey
{
    /// <summary>
    /// How the instant is written before it is hashed: round-trip, always UTC, always seven
    /// fractional digits.
    /// </summary>
    /// <remarks>
    /// The most dangerous field of the six. <c>07:15:30.5+00:00</c> and <c>14:15:30.5+07:00</c> are
    /// the same moment in time and different strings, so hashing them as they arrive gives one
    /// measurement two identities — and the deduplication step then reports success on a row it has
    /// just stored for the second time. Nothing raises an error; the count is simply too high.
    /// <para>
    /// NV1 is UTC+7 with no daylight saving and DE1 observes it, so both spellings genuinely occur.
    /// </para>
    /// </remarks>
    private const string TimestampFormat = "O";

    private MeasurementNaturalKey(
        string siteId,
        EquipmentPath equipmentPath,
        string? unitId,
        string stepCode,
        DateTimeOffset deviceTimestamp,
        string signalCode,
        IdempotencyKey sourceEventId)
    {
        SiteId = siteId;
        EquipmentPath = equipmentPath;
        UnitId = unitId;
        StepCode = stepCode;
        DeviceTimestamp = deviceTimestamp;
        SignalCode = signalCode;
        SourceEventId = sourceEventId;
    }

    /// <summary>The plant. Never null — a measurement belongs to exactly one (K3).</summary>
    public string SiteId { get; }

    /// <summary>The machine that produced the reading.</summary>
    public EquipmentPath EquipmentPath { get; }

    /// <summary>The cell, module or pack the reading is about, when one is known.</summary>
    /// <remarks>
    /// Null is normal and not a gap: a coater reports line speed with no unit under it at all, and a
    /// formation channel only knows which cell it holds once one has been loaded.
    /// </remarks>
    public string? UnitId { get; }

    /// <summary>The process step, for example <c>FORM</c>.</summary>
    public string StepCode { get; }

    /// <summary>When the device says it took the reading.</summary>
    public DateTimeOffset DeviceTimestamp { get; }

    /// <summary>What was measured, for example <c>Formation/Voltage</c>.</summary>
    public string SignalCode { get; }

    /// <summary>The identity derived from the six fields. This is <c>source_event_id</c>.</summary>
    public IdempotencyKey SourceEventId { get; }

    /// <summary>Builds the key and derives the identity.</summary>
    /// <param name="equipmentPath">The machine. Must name a plant, so at least site level.</param>
    /// <param name="stepCode">The process step.</param>
    /// <param name="signalCode">What was measured.</param>
    /// <param name="deviceTimestamp">When the device says it measured it.</param>
    /// <param name="unitId">The unit under the machine, when one is known.</param>
    /// <exception cref="ArgumentException">
    /// The path names no plant, or the step or signal code is blank.
    /// </exception>
    public static MeasurementNaturalKey For(
        EquipmentPath equipmentPath,
        string stepCode,
        string signalCode,
        DateTimeOffset deviceTimestamp,
        string? unitId = null)
    {
        ArgumentNullException.ThrowIfNull(equipmentPath);

        // An enterprise-level path has no plant, and K3 has no room for a measurement that belongs to
        // none. It would also make the first field of every such key identical, which is the one
        // property the derivation must not have.
        var siteId = equipmentPath.SiteId
            ?? throw new ArgumentException(
                $"'{equipmentPath.Value}' names no plant, so it cannot have produced a measurement.",
                nameof(equipmentPath));

        if (string.IsNullOrWhiteSpace(stepCode))
        {
            throw new ArgumentException("A measurement needs a step code.", nameof(stepCode));
        }

        if (string.IsNullOrWhiteSpace(signalCode))
        {
            throw new ArgumentException("A measurement needs a signal code.", nameof(signalCode));
        }

        // Order fixed by docs/scope.md §7.2 and never to be rearranged: the parts are
        // length-prefixed, so a different order is a different key, and every row already in the
        // store was keyed with this one.
        //
        // Absent unit id goes in as an empty string rather than being left out. Dropping the part
        // would shorten the tuple, and a five-part key and a six-part key for the same reading are
        // two identities for one fact.
        var sourceEventId = IdempotencyKey.FromNaturalKey(
            siteId,
            equipmentPath.Value,
            unitId ?? string.Empty,
            stepCode,
            deviceTimestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture),
            signalCode);

        return new MeasurementNaturalKey(
            siteId,
            equipmentPath,
            unitId,
            stepCode,
            deviceTimestamp,
            signalCode,
            sourceEventId);
    }

    /// <summary>The six fields as a line, for a log or a rejection message.</summary>
    /// <remarks>
    /// Not the string that is hashed. That one is length-prefixed so it cannot be ambiguous
    /// (<see cref="IdempotencyKey"/>); this one is for a person reading why a message was refused.
    /// </remarks>
    public override string ToString() =>
        string.Join(
            ' ',
            SiteId,
            EquipmentPath.Value,
            UnitId ?? "-",
            StepCode,
            DeviceTimestamp.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture),
            SignalCode);
}
