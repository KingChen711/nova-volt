using System.Globalization;
using Nvm.Contracts.Events.FactoryModel;
using Nvm.Kernel.Commands;

namespace Nvm.FactoryModel.Commands;

/// <summary>Puts a published revision of the factory model in force at one plant.</summary>
/// <param name="IdempotencyKey">Which intention this is. Build it with <see cref="KeyFor"/>.</param>
/// <param name="SiteId">The plant, for example <c>NV1</c>.</param>
/// <param name="Revision">
/// Which document in the catalog to put in force.
/// </param>
/// <remarks>
/// <para>
/// The revision belongs to the <b>document</b>, not to the plant, and activation is per plant. Those
/// two facts together are what allows a staged rollout: a new model can be adopted at Hai Phong while
/// Leipzig keeps running the previous one, and the two are told apart by which document each is on.
/// </para>
/// <para>
/// <paramref name="Revision"/> selects one immutable document out of
/// <see cref="Storage.IFactoryModelCatalog"/>, which holds every revision that exists rather than only
/// the one in force. Documents are never rewritten, so naming a revision resolves to exactly the tree
/// the caller read — "I looked at revision 12 and I mean to activate that" is a statement the handler
/// can check rather than take on trust.
/// </para>
/// <para>
/// The handler then asks three questions before writing: does the catalog hold this revision, does
/// that document describe this plant, and does it move the plant <b>forward</b> from whatever revision
/// is in force there now. The write itself is a compare-and-swap against that same current revision,
/// so two activations racing for one plant cannot both succeed. Activating a plant model nobody read
/// is how a decommissioned work cell reappears on the shop floor.
/// </para>
/// </remarks>
public sealed record ActivateFactoryModelRevisionCommand(
    IdempotencyKey IdempotencyKey,
    string SiteId,
    int Revision) : ICommand<FactoryModelRevisionActivated>
{
    /// <summary>Derives the idempotency key for activating a revision at a plant.</summary>
    /// <remarks>
    /// The natural key lives next to the command it identifies, so that every caller derives the same
    /// value instead of inventing one. Activating revision 12 at NV1 is one intention however many
    /// times it is asked for — a retry after a timeout has to land on the same key as the attempt that
    /// timed out, or the model gets activated twice and two events go out for one change.
    /// </remarks>
    public static IdempotencyKey KeyFor(string siteId, int revision) =>
        IdempotencyKey.FromNaturalKey(
            "factory-model",
            "revision-activated",
            siteId,
            revision.ToString(CultureInfo.InvariantCulture));
}
