using Nvm.Foundation;

namespace Nvm.Product;

// Lớp Product (vai nhà cung cấp sản phẩm): ba functional block theo domain, một base app công bố toàn bộ.
// Chỉ dựng hành vi đã quan sát hoặc đọc chắc (plan §4.3); chỗ chưa rõ ghi trong docs/oef-mapping.md.

[FunctionalBlock("FB_MS_Product", ModelDomain.MasterData)]
public sealed class ProductMasterBlock;

[FunctionalBlock("FB_OP_Product", ModelDomain.OperationalData)]
public sealed class ProductOperationBlock;

[FunctionalBlock("FB_RF_Product", ModelDomain.ReferenceData)]
public sealed class ProductReferenceBlock;

/// <summary>Response chung của command lớp Product: Id và NId của đối tượng vừa tạo hoặc vừa sửa.</summary>
public sealed class ProductResponse : CommandResponse
{
    public Guid? Id { get; set; }

    public string? NId { get; set; }

    internal static ProductResponse Of(Entity item) => new() { Id = item.Id, NId = item.NId };

    internal static ProductResponse Fail(int code, string message)
    {
        var response = new ProductResponse();
        response.SetError(code, message);
        return response;
    }
}

/// <summary>Mã lỗi của lớp Product. Foundation dùng 70xx–71xx.</summary>
public static class ProductErrors
{
    public const int UnknownEquipment = 7201;
    public const int InvalidEquipmentLevel = 7202;
    public const int UnknownMaterial = 7203;
    public const int UnknownProcess = 7204;
    public const int DuplicateNId = 7205;

    public const int UnknownMtu = 7301;
    public const int SerialQuantity = 7302;
    public const int InsufficientQuantity = 7303;
    public const int UnknownContainer = 7304;
    public const int ContainerCycle = 7305;
    public const int InsideContainer = 7306;
    public const int InvalidQuantity = 7307;
    public const int InvalidContainerStatus = 7308;
    public const int ContainerNotEmpty = 7309;
    public const int MtuNotShippable = 7310;

    public const int UnknownWorkOrder = 7401;
    public const int UnknownWorkOrderOperation = 7402;

    public const int UnknownNonConformance = 7501;
    public const int MtuScrapped = 7502;
    public const int InvalidNonConformanceStatus = 7503;
    public const int MtuNotReleasable = 7504;

    public const int UnknownContainmentRequest = 7601;
    public const int ContainmentClosed = 7602;

    public const int UnknownBuffer = 7701;
    public const int AlreadyInBuffer = 7702;
    public const int NotWhitelisted = 7703;
    public const int NotInBuffer = 7704;
    public const int UnknownLogisticRequest = 7705;
    public const int InvalidLogisticStatus = 7706;
}

/// <summary>Trạng thái MTU có sẵn của nhà cung cấp. Không phải state machine dạng dữ liệu: NC và containment đổi trực tiếp.</summary>
public static class MtuStatus
{
    public const string Available = "Available";
    public const string OnHold = "OnHold";
    public const string Scrapped = "Scrapped";
    public const string Repair = "Repair";

    /// <summary>Đã giao cho khách: không còn ở nhà máy, không nhận NC hay giữ hàng nữa.</summary>
    public const string Shipped = "Shipped";
}

[App(Name)]
public sealed class ProductionApp : AppDefinition
{
    public const string Name = "Production";

    protected override void Define(PomBuilder pom) => pom
        .Entity<Equipment>().Entity<EquipmentProperty>().Entity<EquipmentGroupMember>()
        .Entity<Material>().Entity<MaterialRevision>().Entity<MaterialProperty>()
        .Entity<Process>().Entity<ProcessOperation>().Entity<OperationEquipment>().Entity<OperationMaterial>()
        .Entity<Mtu>().Entity<Mtua>().Entity<MtuHistory>()
        .Entity<WorkOrder>().Entity<WorkOrderOperation>().Entity<WorkOrderOperationEquipment>()
        .Entity<WorkOrderOperationMaterial>().Entity<WorkOrderOutput>().Entity<MaterialConsumption>()
        .Entity<Defect>().Entity<NonConformance>().Entity<NonConformanceItem>()
        .Entity<ContainmentRequest>().Entity<ContainmentItem>()
        .Entity<Buffer>().Entity<BufferWhitelist>().Entity<BufferAssignment>().Entity<LogisticRequest>()
        .Command<CreateEquipment>().Command<SetEquipmentProperty>().Command<AddEquipmentToGroup>()
        .Command<CreateMaterial>().Command<UpdateMaterial>().Command<CreateMaterialRevision>().Command<SetMaterialProperty>()
        .Command<CreateProcess>().Command<AddProcessOperation>().Command<AllowOperationEquipment>().Command<AddOperationMaterial>()
        .Command<CreateMtu>().Command<EditMtu>().Command<MoveMtu>().Command<SplitMtu>().Command<ShipMtu>()
        .Command<CreateMtua>().Command<AssembleIntoMtua>().Command<RemoveFromMtua>().Command<SetMtuaStatus>().Command<SetMtuaStatusForced>()
        .Command<UpdateMtua>().Command<MoveMtua>().Command<DeleteMtua>()
        .Command<InstallWorkOrderStates>().Command<CreateWorkOrderFromProcess>().Command<EditWorkOrder>()
        .Command<ReleaseWorkOrder>().Command<SetWorkOrderStatus>().Command<SetWorkOrderStatusForced>()
        .Command<AbortWorkOrder>().Command<DeleteWorkOrder>()
        .Command<RecordWorkOrderOutput>().Command<RecordOperationProgress>().Command<RecordConsumption>()
        .Command<DefineDefect>().Command<CreateNonConformance>().Command<SetNonConformanceStatus>()
        .Command<HoldMtus>().Command<ReleaseMtus>()
        .Command<ImportContainmentRequest>().Command<ReleaseContainmentRequest>().Command<CloseContainmentRequest>()
        .Command<CreateBuffer>().Command<AddBufferWhitelist>().Command<AttachMtuToBuffer>().Command<DetachMtuFromBuffer>()
        .Command<CreateLogisticRequest>().Command<ReleaseLogisticRequest>().Command<MoveLogisticRequestWithinFlow>()
        .Command<CompleteLogisticRequest>();
}
