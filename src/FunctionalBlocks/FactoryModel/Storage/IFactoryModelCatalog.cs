using Nvm.FactoryModel.Entities;

namespace Nvm.FactoryModel.Storage;

/// <summary>Every revision of the factory model that exists, whether in force or not.</summary>
/// <remarks>
/// <para>
/// <b>A revision is a document, and documents are not edited.</b> Adding a charging channel produces
/// a new document at a higher revision; the previous one stays exactly as it was. That is the same
/// rule K4 puts on the event store, applied to master data — and it is not bookkeeping neatness. A
/// traceability record written last March names
/// <c>NOVAVOLT/NV1/FORMATION/F1/FORM-02</c>, and an auditor asking which line that cycler belonged to
/// can only be answered by the document that was in force in March. Overwrite it and the answer is
/// gone for good.
/// </para>
/// <para>
/// <b>Separate from what is in force.</b> This is the library; <see cref="IActiveFactoryModel"/> is
/// which volume each plant currently has open. Keeping them apart is what makes a staged rollout
/// expressible at all: NV1 running revision 3 while DE1 is still on 1 is two different answers drawn
/// from one shelf, not two shelves.
/// </para>
/// <para>
/// The catalog is read-only here. Publishing a new revision is done by putting a new document in the
/// seed directory, which is the M1 stand-in for the import path M11 will bring — and it is why the
/// interface has no Add: nothing in the running system may invent a revision.
/// </para>
/// </remarks>
public interface IFactoryModelCatalog
{
    /// <summary>Which revisions the catalog holds, ascending. Never empty.</summary>
    /// <remarks>
    /// Gaps are legal. A catalog holding 1, 2 and 5 describes a plant whose revisions 3 and 4 were
    /// drafted and never published, and refusing to load that would be inventing a rule the business
    /// does not have.
    /// </remarks>
    IReadOnlyList<int> Revisions { get; }

    /// <summary>The highest revision on the shelf. Not necessarily the one any plant is running.</summary>
    int LatestRevision { get; }

    /// <summary>Returns the document for a revision, or null when the catalog does not hold it.</summary>
    /// <param name="revision">The revision the caller says it read.</param>
    /// <remarks>
    /// Null rather than an exception: asking for a revision that does not exist is a normal thing for
    /// a caller working from stale information to do, and the handler turns it into a refusal that
    /// names what is available.
    /// </remarks>
    FactoryModelSnapshot? Find(int revision);
}
