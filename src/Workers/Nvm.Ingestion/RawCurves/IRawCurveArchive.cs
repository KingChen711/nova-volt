namespace Nvm.Ingestion.RawCurves;

/// <summary>Keeps the exact bytes a machine produced, and an auditable index of them.</summary>
/// <remarks>
/// An interface so the file-drop adapter can depend on the act of archiving rather than on S3. The
/// adapter's job is to notice that a file is a plant record and to say who handed it over; where the
/// bytes land, and under what object lock, is a decision the adapter should not be able to weaken by
/// accident.
/// </remarks>
public interface IRawCurveArchive
{
    /// <summary>Archives one exact stream idempotently and returns its immutable object version.</summary>
    /// <param name="descriptor">The plant, machine and interval the bytes belong to.</param>
    /// <param name="source">The original bytes, seekable and positioned at the start.</param>
    /// <param name="provenance">Who is archiving, why, and what this corrects (K5).</param>
    /// <param name="cancellationToken">Stops the work when the host shuts down.</param>
    Task<RawCurveArchiveResult> ArchiveAsync(
        RawCurveDescriptor descriptor,
        Stream source,
        RawCurveProvenance provenance,
        CancellationToken cancellationToken);
}
