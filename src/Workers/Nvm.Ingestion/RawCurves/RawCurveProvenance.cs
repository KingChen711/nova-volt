namespace Nvm.Ingestion.RawCurves;

/// <summary>Who archived a raw curve, why, and which record it corrects.</summary>
/// <remarks>
/// <para>
/// K5 says a mistake is fixed by a compensating entry carrying a reason and the person who made it.
/// The archive table was append-only from the start, so a correction was already a second row — but
/// the second row said nothing about itself, and two rows for the same channel and interval with no
/// explanation is a chain of evidence an auditor cannot walk.
/// </para>
/// <para>
/// A required parameter rather than an optional one. A default would be filled in by every caller
/// that had nothing to say, and a column full of "system" answers no question at all.
/// </para>
/// </remarks>
/// <param name="Actor">
/// Who caused this archive: a person, or a named automated caller such as the file-drop adapter.
/// Never a bare service account — "which service wrote it" is already in the object metadata.
/// </param>
/// <param name="Reason">
/// Why these bytes are being archived. For a correction, what was wrong with the record it replaces.
/// </param>
/// <param name="SupersedesArchiveId">
/// The archive record this one corrects, or null for an original. The superseded row and its object
/// both stay: replacing evidence means adding the newer statement beside it, not removing the older.
/// </param>
public sealed record RawCurveProvenance(string Actor, string Reason, Guid? SupersedesArchiveId = null)
{
    /// <summary>Validates the two fields the database also refuses to accept blank.</summary>
    /// <exception cref="ArgumentException">The actor or the reason is missing.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason);
    }
}
