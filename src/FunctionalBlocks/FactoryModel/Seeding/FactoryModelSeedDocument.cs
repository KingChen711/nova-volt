using System.Text.Json.Serialization;

namespace Nvm.FactoryModel.Seeding;

/// <summary>The shape of <c>deploy/seed/factory-model.json</c> as it sits on disk.</summary>
/// <param name="Revision">Which revision of the plant this file describes. Starts at 1.</param>
/// <param name="GeneratedAt">When the file was produced, with an explicit offset.</param>
/// <param name="Enterprise">The root of the tree.</param>
/// <remarks>
/// Deliberately separate from anything that travels on the bus. This is an <i>import</i> format —
/// today a hand-maintained file, at M11 whatever an engineering system exports — and it changes for
/// completely different reasons than a wire contract does. Sharing one serializer configuration
/// between them would tie those reasons together.
/// </remarks>
internal sealed record FactoryModelSeedDocument(
    int Revision,
    DateTimeOffset GeneratedAt,
    FactoryNodeSeed Enterprise);

/// <summary>One node in the seed file, at any level.</summary>
/// <param name="Code">The node's own code, upper case.</param>
/// <param name="Name">Display name.</param>
/// <param name="TimeZoneId">
/// IANA time zone. Present only on a site, and required there.
/// </param>
/// <param name="Children">The nodes one level down, if any.</param>
/// <remarks>
/// One shape for every level rather than five named ones. The level is not written down anywhere in
/// the file: it follows from how deep the node sits, exactly as it does in an
/// <see cref="Kernel.Identity.EquipmentPath"/>. Naming the levels in the file would create a second
/// place for them to be stated, and therefore a place for them to disagree.
/// </remarks>
internal sealed record FactoryNodeSeed(
    string Code,
    string Name,
    string? TimeZoneId = null,
    IReadOnlyList<FactoryNodeSeed>? Children = null);

/// <summary>Serializer configuration for the seed file, and nothing else.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FactoryModelSeedDocument))]
internal sealed partial class FactoryModelSeedJsonContext : JsonSerializerContext;
