using Nvm.Foundation;

namespace Nvm.FoundationTests.LabModel;

// Model thử nghiệm cho test hành vi của Foundation. Ba block: master data, dữ liệu vận hành của "nhà cung cấp",
// và block "của team" gắn facet, pre-check, post-action vào command của nhà cung cấp mà không sửa nó.

[FunctionalBlock("FB_MS_Lab", ModelDomain.MasterData)]
public sealed class LabMasterBlock;

[FunctionalBlock("FB_OP_Lab", ModelDomain.OperationalData)]
public sealed class LabOperationBlock;

[FunctionalBlock("FB_OP_LabExt", ModelDomain.OperationalData)]
public sealed class LabExtensionBlock;

public static class LabApp
{
    public const string Name = "LabApp";
    public const string OtherName = "OtherApp";
}

[Entity<LabMasterBlock>]
public sealed class LabMaterial : Entity
{
    [Mandatory]
    public string? UnitOfMeasure { get; set; }

    public string? Description { get; set; }
}

[Entity<LabOperationBlock>]
public sealed class LabOrder : Entity
{
    [Mandatory]
    public string? MaterialNId { get; set; }

    public decimal? Quantity { get; set; }
}

[Entity<LabOperationBlock>]
public sealed class LabOrderNote : Entity
{
    public string? OrderNId { get; set; }

    [TextLength(-1)]
    public string? Text { get; set; }
}

[Facet<LabExtensionBlock>]
public sealed class LabOrderCounters : Facet<LabOrder>
{
    public int? CreatedCount { get; set; }
}

[Facet<LabExtensionBlock>]
public sealed class LabOrderSupplier : Facet<LabOrder>
{
    public string? SupplierCode { get; set; }
}

[Event<LabOperationBlock>]
[EventVersion(1)]
public sealed record LabOrderCreated(Guid OrderId, string NId) : IFoundationEvent;

public class LabResponse : CommandResponse;

public sealed class CreatedResponse : CommandResponse
{
    public Guid Id { get; set; }
}

public sealed class CountResponse : CommandResponse
{
    public int InTransaction { get; set; }

    public int Projection { get; set; }
}
