using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.Ingestion.FileDrop;

/// <summary>One measurement read off a CSV line.</summary>
/// <param name="EquipmentPath">Where it was taken, resolved against the plant's model.</param>
/// <param name="UnitId">The unit under the machine, when the file names one.</param>
/// <param name="Reading">
/// The signal, value and measurement time, in the same shape the Sparkplug decoder produces — so
/// that the natural key is built by the same call and cannot drift (C15.1).
/// </param>
public sealed record FileMeasurement(
    EquipmentPath EquipmentPath,
    string? UnitId,
    DeviceReading Reading);

/// <summary>A line the reader could not turn into a measurement, and why.</summary>
/// <param name="LineNumber">1-based line number in the source file, header included.</param>
/// <param name="Line">The line exactly as it was written.</param>
/// <param name="Reason">What was wrong with it, in words an operator can act on.</param>
/// <remarks>
/// The line is kept verbatim. A rejection an operator cannot see the original of is a rejection they
/// have to reproduce before they can fix it, and by then the tester has usually overwritten its own
/// export.
/// </remarks>
public sealed record RejectedLine(int LineNumber, string Line, string Reason);

/// <summary>What one CSV file turned into.</summary>
/// <param name="Measurements">Lines that parsed.</param>
/// <param name="Rejected">Lines that did not.</param>
public sealed record FileDropParseResult(
    IReadOnlyList<FileMeasurement> Measurements,
    IReadOnlyList<RejectedLine> Rejected);
