using System.ComponentModel.DataAnnotations;

namespace Nvm.PublicObjectModel;

public sealed class Equipment
{
    [Key, MaxLength(64)]
    public required string Id { get; init; }

    [MaxLength(3)]
    public required string SiteId { get; init; }

    [MaxLength(256)]
    public required string EquipmentPath { get; init; }

    [MaxLength(128)]
    public required string Name { get; init; }

    [MaxLength(2)]
    public required string Line { get; init; }

    [MaxLength(32)]
    public required string Resource { get; init; }

    [ConcurrencyCheck]
    public int Revision { get; init; }
}
