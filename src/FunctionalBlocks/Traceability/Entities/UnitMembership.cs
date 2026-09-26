using System.Text.Json;
using Nvm.Contracts.Events.Traceability;
using Nvm.Kernel.EventSourcing;
using Nvm.Kernel.Identity;

namespace Nvm.Traceability.Entities;

/// <summary>
/// Unit con đang nằm trong cha nào. Một stream cho mỗi unit con (<c>membership:{serial}</c>), tách khỏi
/// stream của cha để 96 cell lắp vào cùng một pack không tranh chấp nhau (ADR-044).
/// </summary>
public sealed class UnitMembership
{
    public const string StreamType = "unit-membership";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private UnitMembership(string siteId, string childSerialNumber)
    {
        SiteId = siteId;
        ChildSerialNumber = childSerialNumber;
    }

    public string SiteId { get; }
    public string ChildSerialNumber { get; }
    public string? ParentSerialNumber { get; private set; }
    public string? Position { get; private set; }
    public long Version { get; private set; }

    public static string StreamId(string childSerialNumber) => "membership:" + childSerialNumber;

    /// <summary>Unit con được phép vào loại cha nào: cell→module, cell→pack (cell-to-pack), module→pack.</summary>
    public static bool CanContain(ProductionUnitKind parent, ProductionUnitKind child) => (parent, child) switch
    {
        (ProductionUnitKind.Module, ProductionUnitKind.Cell) => true,
        (ProductionUnitKind.Pack, ProductionUnitKind.Cell) => true,
        (ProductionUnitKind.Pack, ProductionUnitKind.Module) => true,
        _ => false
    };

    public static UnitMembership Replay(string siteId, string childSerialNumber, EventStream? stream)
    {
        var membership = new UnitMembership(siteId, childSerialNumber);
        if (stream is null)
        { return membership; }
        foreach (var stored in stream.Events)
        {
            if (stored.Version != membership.Version + 1 || stored.SiteId != siteId)
            { throw new InvalidDataException("Membership stream order or site is invalid."); }
            switch (stored.EventType)
            {
                case "com.novavolt.traceability.unit-assembled-into.v1":
                    var assembled = Read<UnitAssembledInto>(stored);
                    Ensure(assembled.ChildSerialNumber == childSerialNumber && membership.ParentSerialNumber is null);
                    membership.ParentSerialNumber = assembled.ParentSerialNumber;
                    membership.Position = assembled.Position;
                    break;
                case "com.novavolt.traceability.unit-removed-from.v1":
                    var removed = Read<UnitRemovedFrom>(stored);
                    Ensure(removed.ChildSerialNumber == childSerialNumber &&
                        membership.ParentSerialNumber == removed.ParentSerialNumber);
                    membership.ParentSerialNumber = null;
                    membership.Position = null;
                    break;
                case "com.novavolt.traceability.genealogy-correction-recorded.v1":
                    var corrected = Read<GenealogyCorrectionRecorded>(stored);
                    Ensure(corrected.ChildSerialNumber == childSerialNumber &&
                        membership.ParentSerialNumber == corrected.WrongParentSerialNumber);
                    membership.ParentSerialNumber = corrected.CorrectParentSerialNumber;
                    membership.Position = corrected.Position;
                    break;
                default:
                    throw new InvalidDataException($"Unknown membership event: {stored.EventType}.");
            }
            membership.Version = stored.Version;
        }
        return membership;
    }

    private static T Read<T>(StoredStreamEvent stored) where T : class
    {
        if (stored.SchemaVersion != 1)
        { throw new InvalidDataException($"Unexpected schema version {stored.SchemaVersion}."); }
        return JsonSerializer.Deserialize<T>(stored.PayloadJson, Json)
            ?? throw new InvalidDataException("Membership event payload is null.");
    }

    private static void Ensure(bool condition)
    {
        if (!condition)
        { throw new InvalidDataException("Membership history is inconsistent."); }
    }
}
