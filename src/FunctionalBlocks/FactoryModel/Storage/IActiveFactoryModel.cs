using Nvm.FactoryModel.Entities;

namespace Nvm.FactoryModel.Storage;

/// <summary>The model a plant is running, and which document revision it came from.</summary>
/// <param name="Revision">The document revision this model was taken from.</param>
/// <param name="Site">The plant's tree as it stood at that revision.</param>
/// <remarks>
/// The revision belongs to the document and the activation belongs to the plant, so the pairing has
/// to be recorded somewhere — a <see cref="FactorySite"/> read out of a file does not know which
/// revision of that file it came from. Keeping the two together is what lets Hai Phong sit on
/// revision 12 while Leipzig is still on 11.
/// </remarks>
public sealed record ActiveFactoryModelRevision(int Revision, FactorySite Site)
{
    /// <summary>The plant this applies to.</summary>
    public string SiteId => Site.SiteId;
}

/// <summary>Which revision of the model is in force at each plant right now.</summary>
/// <remarks>
/// <para>
/// Separate from the seed file, because "what the document offers" and "what the plant is running"
/// are different questions. A document can sit on disk for a week before anyone activates it, and two
/// plants can be running models from different documents at the same time — which is what a staged
/// rollout looks like.
/// </para>
/// <para>
/// In-memory today. The version that matters keeps this in the database, so a restart does not put
/// every plant back on whatever the seed file happens to say.
/// </para>
/// </remarks>
public interface IActiveFactoryModel
{
    /// <summary>What a plant is running, or null when nothing has been activated there yet.</summary>
    ActiveFactoryModelRevision? Current(string siteId);

    /// <summary>Puts a revision in force, but only if the plant has not moved since it was read.</summary>
    /// <param name="revision">The revision to put in force.</param>
    /// <param name="expectedCurrentRevision">
    /// The revision number <see cref="Current"/> returned, or null if it returned nothing.
    /// </param>
    /// <returns>False when the plant moved in between, and nothing was written.</returns>
    /// <remarks>
    /// <para>
    /// Compare-and-swap rather than a plain write, because deciding and writing are two steps and
    /// something can happen in between. Two activations landing together — a scheduled rollout and an
    /// engineer pressing the button — both read revision 2, both find their own revision newer, and
    /// both write. The plant ends up on whichever finished last, and two events go out each claiming
    /// to have moved it forward from 2. A consumer rebuilding its cache from those cannot tell which
    /// tree the plant is actually running.
    /// </para>
    /// <para>
    /// The revision number doubles as the version token, so there is nothing extra to store. This is
    /// the same optimistic concurrency the event store uses with <c>expectedVersion</c> at M5;
    /// meeting it here first is deliberate.
    /// </para>
    /// </remarks>
    bool TryActivate(ActiveFactoryModelRevision revision, int? expectedCurrentRevision);
}
