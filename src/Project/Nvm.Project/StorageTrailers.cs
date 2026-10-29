using Nvm.Foundation;
using Nvm.Product;

namespace Nvm.Project;

// Trailer, phần của team (app hậu cần bên ngoài YardApp): ở R7 chỉ phần trailer kho, tức trailer đỗ trong nhà máy làm kho tạm
// cho pallet thành phẩm, và yêu cầu kéo xe giữa yard và dock. Lịch xe, placard, nhận skid thuộc R9.

[FunctionalBlock("FB_OP_Yard", ModelDomain.OperationalData)]
public sealed class YardOperationBlock;

/// <summary>App hậu cần bên ngoài của team: trailer, dock, kéo xe. App độc lập.</summary>
[App(Name)]
public sealed class YardApp : AppDefinition
{
    public const string Name = "YardApp";

    protected override void Define(PomBuilder pom) => pom
        .Entity<Trailer>().Entity<TrailerSchedule>().Entity<TrailerMovement>().Entity<Mtua>().Entity<PackingDetail>()
        .Command<CreateTrailer>().Command<ArriveEmptyTrailer>().Command<SetTrailerAsStorage>().Command<UnsetTrailerAsStorage>()
        .Command<CallStorageTrailer>().Command<CreateTrailerMovementRequest>().Command<UpdateTrailerMovement>()
        .Command<LoadPalletIntoStorageTrailer>().Command<CompleteStorageTrailerLoad>().Command<UnloadStorageTrailer>()
        .Command<UnloadPalletFromStorageTrailer>();
}

public static class TrailerStates
{
    public const string Machine = "TrailerStateMachine";
    public const string Free = "Free";
    public const string Empty = "Empty";
    public const string Tapped = "Tapped";
    public const string Full = "Full";
}

/// <summary>Loại kho của trailer kho.</summary>
public static class StorageTypes
{
    /// <summary>Kho thành phẩm: quét và theo dõi từng pallet.</summary>
    public const string FinishedGoods = "FGStorage";

    /// <summary>Kho chung: khai đầy và khai dỡ bằng ghi chú, không theo dõi pallet.</summary>
    public const string Generic = "GenericStorage";

    public static readonly IReadOnlyList<string> All = [FinishedGoods, Generic];
}

public static class ScheduleStates
{
    public const string ToBeLoaded = "ToBeLoaded";
    public const string Tapped = "Tapped";
    public const string Loaded = "Loaded";
    public const string ToCallForShipping = "ToCallForShipping";
    public const string ToCallForTest = "ToCallForTest";
    public const string Unloaded = "Unloaded";
    public const string Closed = "CompletedReloaded";

    public static readonly IReadOnlyList<string> Closing = [Unloaded, Closed];
}

public static class MovementStates
{
    public const string Created = "Created";
    public const string Accepted = "Accepted";
    public const string Picked = "Picked";
    public const string Dropped = "Dropped";

    public static readonly IReadOnlyList<string> Order = [Created, Accepted, Picked, Dropped];
}

/// <summary>Trailer theo barcode (NId). Trạng thái theo state machine dạng dữ liệu; vị trí là một ô yard hoặc cửa dock.</summary>
[Entity<YardOperationBlock>]
public sealed class Trailer : Entity, IHasStatus
{
    public string? TrailerNo { get; set; }

    public string? Status { get; set; }

    /// <summary>Loại kho (<see cref="StorageTypes"/>); rỗng là trailer thường.</summary>
    public string? ContentType { get; set; }

    public string? LocationNId { get; set; }

    /// <summary>MTUA mang barcode của trailer, chứa các pallet; chỉ có khi trailer là trailer kho.</summary>
    public string? StorageMtuaNId { get; set; }
}

/// <summary>Lịch chất hàng của trailer kho ở một cửa dock. Mỗi trailer tối đa một lịch đang mở.</summary>
[Entity<YardOperationBlock>]
public sealed class TrailerSchedule : Entity
{
    [Mandatory]
    public string? TrailerNId { get; set; }

    public string? DockNId { get; set; }

    public string? Status { get; set; }

    [TextLength(-1)]
    public string? Comments { get; set; }
}

/// <summary>Yêu cầu kéo trailer tới một vị trí: Created → Accepted → Picked → Dropped; tới Dropped thì trailer ở đích.</summary>
[Entity<YardOperationBlock>]
public sealed class TrailerMovement : Entity
{
    [Mandatory]
    public string? TrailerNId { get; set; }

    public string? FromNId { get; set; }

    [Mandatory]
    public string? ToNId { get; set; }

    public string? Priority { get; set; }

    public string? Status { get; set; }
}

public sealed class YardResponse : CommandResponse
{
    public string? NId { get; set; }

    public string? Status { get; set; }

    internal static YardResponse Fail(int code, string message)
    {
        var response = new YardResponse();
        response.SetError(code, message);
        return response;
    }

    internal static YardResponse From(CommandResponse failed) => Fail(failed.Error!.ErrorCode, failed.Error.ErrorMessage);

    internal static YardResponse Of(Trailer trailer) => new() { NId = trailer.NId, Status = trailer.Status };
}

internal static class Yard
{
    /// <summary>Equipment là cửa dock: có property <c>LocationType</c> = <c>Dock</c>.</summary>
    public static bool IsDock(IReadOnlyPlatform platform, string? equipmentNId)
    {
        var nid = equipmentNId + "|LocationType";
        return platform.ProjectionQuery<EquipmentProperty>().Any(p => p.NId == nid && p.Value == "Dock");
    }

    public static YardResponse? Find(IPlatform platform, string barcode, out Trailer trailer)
    {
        trailer = platform.Query<Trailer>().SingleOrDefault(t => t.NId == barcode)!;
        return trailer is null ? YardResponse.Fail(NvErrors.UnknownTrailer, $"Trailer {barcode} does not exist.") : null;
    }

    public static TrailerSchedule? OpenSchedule(IPlatform platform, string trailer) =>
        platform.Query<TrailerSchedule>().Where(s => s.TrailerNId == trailer && !ScheduleStates.Closing.Contains(s.Status!))
            .OrderByDescending(s => s.CreatedOn).FirstOrDefault();

    public static void SetStatus(IPlatform platform, Trailer trailer, string status)
    {
        if (trailer.Status != status)
        {
            platform.ChangeStatus(trailer, TrailerStates.Machine, status);
        }
    }

    public static List<Mtua> Pallets(IPlatform platform, Trailer trailer) =>
        [.. platform.Query<Mtua>().Where(c => c.ParentMtuaNId == trailer.StorageMtuaNId).OrderBy(c => c.NId)];

    /// <summary>Tạo yêu cầu kéo xe: đích có thật và khác vị trí hiện tại; trailer chưa có yêu cầu đang mở; đích chưa có xe hay yêu cầu.</summary>
    public static YardResponse Move(IPlatform platform, Trailer trailer, string destination, string? priority)
    {
        if (!platform.ProjectionQuery<Equipment>().Any(e => e.NId == destination))
        {
            return YardResponse.Fail(NvErrors.UnknownLocation, $"Location {destination} does not exist.");
        }

        if (destination == trailer.LocationNId)
        {
            return YardResponse.Fail(NvErrors.InvalidMovement, $"Trailer {trailer.NId} is already at {destination}.");
        }

        var open = platform.Query<TrailerMovement>().Where(m => m.Status != MovementStates.Dropped);
        if (open.Any(m => m.TrailerNId == trailer.NId))
        {
            return YardResponse.Fail(NvErrors.MovementOpen, $"Trailer {trailer.NId} already has an open movement request.");
        }

        if (open.Any(m => m.ToNId == destination) || platform.Query<Trailer>().Any(t => t.LocationNId == destination))
        {
            return YardResponse.Fail(NvErrors.LocationBusy, $"Location {destination} has a trailer or a request already.");
        }

        var movement = platform.Create<TrailerMovement>();
        movement.NId = movement.Id.ToString("N");
        movement.TrailerNId = trailer.NId;
        movement.FromNId = trailer.LocationNId;
        movement.ToNId = destination;
        movement.Priority = priority ?? "Normal";
        movement.Status = MovementStates.Created;
        platform.Submit(movement);
        return new YardResponse { NId = movement.NId, Status = movement.Status };
    }
}

/// <summary>Khai báo trailer: barcode là NId, Free, chưa có vị trí.</summary>
[Command<YardOperationBlock>]
public sealed class CreateTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    public string? TrailerNo { get; init; }
}

internal sealed class CreateTrailerHandler : ICommandHandler<CreateTrailer, YardResponse>
{
    public YardResponse Handle(CreateTrailer command, IPlatform platform)
    {
        if (platform.Query<Trailer>().Any(t => t.NId == command.Barcode))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {command.Barcode} already exists.");
        }

        var trailer = platform.Create<Trailer>();
        trailer.NId = command.Barcode!;
        trailer.TrailerNo = command.TrailerNo ?? command.Barcode;
        platform.ChangeStatus(trailer, TrailerStates.Machine, TrailerStates.Free);
        return YardResponse.Of(trailer);
    }
}

/// <summary>Trailer rỗng tới và đỗ ở một ô yard: Free → Empty. Lịch xe và placard thuộc R9.</summary>
[Command<YardOperationBlock>]
public sealed class ArriveEmptyTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? LocationNId { get; init; }
}

internal sealed class ArriveEmptyTrailerHandler : ICommandHandler<ArriveEmptyTrailer, YardResponse>
{
    public YardResponse Handle(ArriveEmptyTrailer command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (!platform.ProjectionQuery<Equipment>().Any(e => e.NId == command.LocationNId))
        {
            return YardResponse.Fail(NvErrors.UnknownLocation, $"Location {command.LocationNId} does not exist.");
        }

        if (platform.Query<Trailer>().Any(t => t.LocationNId == command.LocationNId && t.NId != trailer.NId))
        {
            return YardResponse.Fail(NvErrors.LocationBusy, $"Location {command.LocationNId} has a trailer already.");
        }

        trailer.LocationNId = command.LocationNId;
        Yard.SetStatus(platform, trailer, TrailerStates.Empty);
        return YardResponse.Of(trailer);
    }
}

/// <summary>
/// Đặt loại kho cho trailer rỗng: tạo MTUA mang barcode của trailer (vật chứa loại trailer kho, Empty) ở vị trí của trailer.
/// Trailer đã có loại kho thì phải gỡ trước, không đổi thẳng.
/// </summary>
[Command<YardOperationBlock>]
public sealed class SetTrailerAsStorage : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? ContentType { get; init; }
}

internal sealed class SetTrailerAsStorageHandler : ICommandHandler<SetTrailerAsStorage, YardResponse>
{
    public YardResponse Handle(SetTrailerAsStorage command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (!StorageTypes.All.Contains(command.ContentType))
        {
            return YardResponse.Fail(NvErrors.NoStorageType, $"Storage type must be one of {string.Join(", ", StorageTypes.All)}.");
        }

        if (trailer.Status != TrailerStates.Empty)
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status}; only an empty trailer takes a storage type.");
        }

        if (!string.IsNullOrEmpty(trailer.ContentType))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is already {trailer.ContentType}; unset it first.");
        }

        var box = platform.CallCommand<CreateMtua, ProductResponse>(new CreateMtua
        {
            NId = trailer.NId,
            ContainerType = ContainerTypes.StorageTrailer,
            EquipmentNId = trailer.LocationNId,
            StateMachine = NvStateMachines.StorageTrailer,
            Status = PalletStates.Empty,
        });
        if (!box.Succeeded)
        {
            return YardResponse.From(box);
        }

        trailer.ContentType = command.ContentType;
        trailer.StorageMtuaNId = trailer.NId;
        platform.Submit(trailer);
        return YardResponse.Of(trailer);
    }
}

/// <summary>Gỡ loại kho: MTUA không còn pallet, trailer Empty, không đứng ở cửa dock. Đóng lịch đang mở, xóa mềm MTUA.</summary>
[Command<YardOperationBlock>]
public sealed class UnsetTrailerAsStorage : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }
}

internal sealed class UnsetTrailerAsStorageHandler : ICommandHandler<UnsetTrailerAsStorage, YardResponse>
{
    public YardResponse Handle(UnsetTrailerAsStorage command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (string.IsNullOrEmpty(trailer.StorageMtuaNId))
        {
            return YardResponse.Fail(NvErrors.NoStorageType, $"Trailer {trailer.NId} is not a storage trailer.");
        }

        if (Yard.Pallets(platform, trailer).Count > 0 || trailer.Status != TrailerStates.Empty)
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is not empty.");
        }

        if (Yard.IsDock(platform, trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.TrailerAtDock, $"Trailer {trailer.NId} stands at dock {trailer.LocationNId}; move it out first.");
        }

        foreach (var schedule in platform.Query<TrailerSchedule>().Where(s => s.TrailerNId == trailer.NId && s.Status != ScheduleStates.Closed).ToList())
        {
            schedule.Status = ScheduleStates.Closed;
            platform.Submit(schedule);
        }

        var deleted = platform.CallCommand<DeleteMtua, ProductResponse>(new DeleteMtua { NId = trailer.StorageMtuaNId });
        if (!deleted.Succeeded)
        {
            return YardResponse.From(deleted);
        }

        trailer.ContentType = null;
        trailer.StorageMtuaNId = null;
        platform.Submit(trailer);
        return YardResponse.Of(trailer);
    }
}

/// <summary>
/// Gọi trailer kho từ yard vào một cửa dock: không có lịch đang mở thì tạo lịch chất hàng ToBeLoaded ở cửa đó; rồi tạo yêu cầu
/// kéo xe.
/// </summary>
[Command<YardOperationBlock>]
public sealed class CallStorageTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? DockNId { get; init; }

    public string? Priority { get; init; }
}

internal sealed class CallStorageTrailerHandler : ICommandHandler<CallStorageTrailer, YardResponse>
{
    public YardResponse Handle(CallStorageTrailer command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (string.IsNullOrEmpty(trailer.StorageMtuaNId))
        {
            return YardResponse.Fail(NvErrors.NoStorageType, $"Trailer {trailer.NId} has no storage type.");
        }

        if (!Yard.IsDock(platform, command.DockNId))
        {
            return YardResponse.Fail(NvErrors.TrailerNotAtDock, $"{command.DockNId} is not a dock door.");
        }

        if (Yard.OpenSchedule(platform, trailer.NId) is null)
        {
            var schedule = platform.Create<TrailerSchedule>();
            schedule.NId = schedule.Id.ToString("N");
            schedule.TrailerNId = trailer.NId;
            schedule.DockNId = command.DockNId;
            schedule.Status = ScheduleStates.ToBeLoaded;
            platform.Submit(schedule);
        }

        return Yard.Move(platform, trailer, command.DockNId!, command.Priority);
    }
}

/// <summary>Người điều phối dời trailer tới một vị trí bất kỳ (yard hay dock).</summary>
[Command<YardOperationBlock>]
public sealed class CreateTrailerMovementRequest : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? DestinationNId { get; init; }

    public string? Priority { get; init; }
}

internal sealed class CreateTrailerMovementRequestHandler : ICommandHandler<CreateTrailerMovementRequest, YardResponse>
{
    public YardResponse Handle(CreateTrailerMovementRequest command, IPlatform platform) =>
        Yard.Find(platform, command.Barcode!, out var trailer) ?? Yard.Move(platform, trailer, command.DestinationNId!, command.Priority);
}

/// <summary>
/// Người kéo xe đi tiếp một bước của yêu cầu (không nhảy bước). Tới Dropped: trailer ở đích, MTUA kho của nó (và pallet bên trong)
/// đi theo.
/// </summary>
[Command<YardOperationBlock>]
public sealed class UpdateTrailerMovement : ICommand<YardResponse>
{
    [Mandatory]
    public string? MovementNId { get; init; }

    [Mandatory]
    public string? Status { get; init; }
}

internal sealed class UpdateTrailerMovementHandler : ICommandHandler<UpdateTrailerMovement, YardResponse>
{
    public YardResponse Handle(UpdateTrailerMovement command, IPlatform platform)
    {
        var movement = platform.Query<TrailerMovement>().SingleOrDefault(m => m.NId == command.MovementNId);
        if (movement is null)
        {
            return YardResponse.Fail(NvErrors.InvalidMovement, $"Movement request {command.MovementNId} does not exist.");
        }

        var next = MovementStates.Order.ToList().IndexOf(movement.Status!) + 1;
        if (next >= MovementStates.Order.Count || MovementStates.Order[next] != command.Status)
        {
            return YardResponse.Fail(NvErrors.InvalidMovement, $"Movement request {movement.NId} is {movement.Status}; it cannot go to {command.Status}.");
        }

        movement.Status = command.Status;
        platform.Submit(movement);
        if (command.Status == MovementStates.Dropped)
        {
            var trailer = platform.Query<Trailer>().Single(t => t.NId == movement.TrailerNId);
            trailer.LocationNId = movement.ToNId;
            platform.Submit(trailer);
            if (!string.IsNullOrEmpty(trailer.StorageMtuaNId))
            {
                var moved = platform.CallCommand<MoveMtua, ProductResponse>(new MoveMtua { NId = trailer.StorageMtuaNId, EquipmentNId = movement.ToNId });
                if (!moved.Succeeded)
                {
                    return YardResponse.From(moved);
                }
            }
        }

        return new YardResponse { NId = movement.NId, Status = movement.Status };
    }
}

/// <summary>
/// Nạp một pallet vào trailer kho thành phẩm ở cửa dock. Luật theo thứ tự: không hàng bị giữ hay hủy trong cây của pallet (procedure;
/// key <see cref="NvKeys.StorageTrailerHeldCheck"/> = <c>false</c> thì bỏ); pallet Full; trailer đã có pallet thì cùng material
/// với pallet đầu; không trộn pallet đã đo với chưa đo. Trailer và lịch sang Tapped.
/// </summary>
[Command<YardOperationBlock>]
public sealed class LoadPalletIntoStorageTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? PalletNId { get; init; }
}

internal sealed class LoadPalletIntoStorageTrailerHandler : ICommandHandler<LoadPalletIntoStorageTrailer, YardResponse>
{
    public YardResponse Handle(LoadPalletIntoStorageTrailer command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (trailer.ContentType != StorageTypes.FinishedGoods || !Yard.IsDock(platform, trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.TrailerNotAtDock, $"Trailer {trailer.NId} is not a finished goods storage trailer at a dock door.");
        }

        if (trailer.Status is not (TrailerStates.Empty or TrailerStates.Tapped))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} is {trailer.Status}.");
        }

        var pallet = platform.Query<Mtua>().SingleOrDefault(c => c.NId == command.PalletNId && c.ContainerType == ContainerTypes.Pallet);
        if (pallet is null)
        {
            return YardResponse.Fail(NvErrors.InvalidPallet, $"Pallet {command.PalletNId} does not exist.");
        }

        var check = platform.ProjectionQuery<ConfigurationKey>().Where(k => k.NId == NvKeys.StorageTrailerHeldCheck).Select(k => k.Val).SingleOrDefault();
        if (!string.Equals(check, "false", StringComparison.OrdinalIgnoreCase))
        {
            var held = FinishedGoodsSql.Held(FinishedGoodsSql.Connection(platform.ProjectionQuery<ConfigurationKey>), pallet.NId);
            if (held.Count > 0)
            {
                return YardResponse.Fail(NvErrors.HeldOrScrapped, $"Pallet {pallet.NId} holds {held.Count} item(s) on hold or scrapped.");
            }
        }

        if (pallet.Status != PalletStates.Full)
        {
            return YardResponse.Fail(NvErrors.InvalidPallet, $"Pallet {pallet.NId} is {pallet.Status}; only a full pallet is loaded.");
        }

        if (!string.IsNullOrEmpty(pallet.ParentMtuaNId))
        {
            return YardResponse.Fail(NvErrors.InvalidPallet, $"Pallet {pallet.NId} is inside {pallet.ParentMtuaNId}; unload it there first.");
        }

        var inside = Yard.Pallets(platform, trailer);
        if (inside.Count > 0)
        {
            if (inside[0].MaterialNId != pallet.MaterialNId)
            {
                return YardResponse.Fail(NvErrors.MaterialMismatch,
                    $"Pallet {pallet.NId} is {pallet.MaterialNId}; trailer {trailer.NId} holds {inside[0].MaterialNId}.");
            }

            if (PackingData.EolvRequired(platform, pallet.MaterialNId) && FinishedGoods.Tested(platform, inside[0]) != FinishedGoods.Tested(platform, pallet))
            {
                return YardResponse.Fail(NvErrors.MixedTestState, $"Trailer {trailer.NId} cannot mix tested and untested pallets.");
            }
        }

        var loaded = platform.CallCommand<AssembleIntoMtua, ProductResponse>(new AssembleIntoMtua { ChildNId = pallet.NId, ContainerNId = trailer.StorageMtuaNId });
        var counted = loaded.Succeeded
            ? platform.CallCommand<UpdateMtua, ProductResponse>(new UpdateMtua { NId = trailer.StorageMtuaNId, Quantity = inside.Count + 1, MaterialNId = pallet.MaterialNId })
            : loaded;
        if (!counted.Succeeded)
        {
            return YardResponse.From(counted);
        }

        var detail = PackingData.DetailOrNew(platform, pallet);
        detail.LoadedOn = platform.Time.GetUtcNow();
        platform.Submit(detail);
        Yard.SetStatus(platform, trailer, TrailerStates.Tapped);
        if (Yard.OpenSchedule(platform, trailer.NId) is { } schedule)
        {
            schedule.Status = ScheduleStates.Tapped;
            platform.Submit(schedule);
        }

        return YardResponse.Of(trailer);
    }
}

/// <summary>
/// Chốt trailer kho đầy. Loại thành phẩm: trailer phải đang Tapped; lịch chờ gọi đi giao, hoặc chờ đo nếu có pallet chưa đo; phát
/// <see cref="PalletLocated"/> cho từng pallet. Loại chung: trailer rỗng khai đầy bằng ghi chú, lịch Loaded.
/// </summary>
[Command<YardOperationBlock>]
public sealed class CompleteStorageTrailerLoad : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    public string? Comments { get; init; }
}

internal sealed class CompleteStorageTrailerLoadHandler : ICommandHandler<CompleteStorageTrailerLoad, YardResponse>
{
    public YardResponse Handle(CompleteStorageTrailerLoad command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        var generic = trailer.ContentType == StorageTypes.Generic;
        if (string.IsNullOrEmpty(trailer.StorageMtuaNId) || trailer.Status != (generic ? TrailerStates.Empty : TrailerStates.Tapped))
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} ({trailer.ContentType}, {trailer.Status}) cannot be completed.");
        }

        var schedule = Yard.OpenSchedule(platform, trailer.NId);
        if (schedule is null)
        {
            return YardResponse.Fail(NvErrors.InvalidTrailerState, $"Trailer {trailer.NId} has no open loading schedule.");
        }

        if (generic)
        {
            schedule.Status = ScheduleStates.Loaded;
            schedule.Comments = Append(schedule.Comments, command.Comments);
        }
        else
        {
            var pallets = Yard.Pallets(platform, trailer);
            schedule.Status = pallets.All(p => FinishedGoods.UntestedItem(platform, p) is null) ? ScheduleStates.ToCallForShipping : ScheduleStates.ToCallForTest;
            foreach (var pallet in pallets)
            {
                platform.RaiseEventOnCommit(new PalletLocated(pallet.NId, trailer.LocationNId));
            }
        }

        platform.Submit(schedule);
        var full = platform.CallCommand<SetMtuaStatus, ProductResponse>(new SetMtuaStatus { NId = trailer.StorageMtuaNId, Status = PalletStates.Full });
        if (!full.Succeeded)
        {
            return YardResponse.From(full);
        }

        Yard.SetStatus(platform, trailer, TrailerStates.Full);
        return YardResponse.Of(trailer);
    }

    internal static string Append(string? comments, string? line) =>
        string.IsNullOrEmpty(line) ? comments ?? string.Empty : string.IsNullOrEmpty(comments) ? line : comments + Environment.NewLine + line;
}

/// <summary>
/// Dỡ cả trailer kho về cửa dock nó đang đứng: mỗi pallet về vị trí của trailer (qua lệnh chuyển vật chứa của lớp Product), MTUA
/// trailer số lượng 0 và Empty, trailer Empty, lịch Unloaded. Loại kho giữ nguyên. Loại chung chỉ ghi ghi chú.
/// </summary>
[Command<YardOperationBlock>]
public sealed class UnloadStorageTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    public string? Comments { get; init; }
}

internal sealed class UnloadStorageTrailerHandler : ICommandHandler<UnloadStorageTrailer, YardResponse>
{
    public YardResponse Handle(UnloadStorageTrailer command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (string.IsNullOrEmpty(trailer.StorageMtuaNId) || !Yard.IsDock(platform, trailer.LocationNId))
        {
            return YardResponse.Fail(NvErrors.TrailerNotAtDock, $"Trailer {trailer.NId} is not a storage trailer at a dock door.");
        }

        foreach (var pallet in Yard.Pallets(platform, trailer))
        {
            var moved = platform.CallCommand<MoveMtua, ProductResponse>(new MoveMtua { NId = pallet.NId, EquipmentNId = trailer.LocationNId });
            if (!moved.Succeeded)
            {
                return YardResponse.From(moved);
            }
        }

        var box = platform.CallCommand<UpdateMtua, ProductResponse>(new UpdateMtua { NId = trailer.StorageMtuaNId, Quantity = 0m, ClearMaterial = true });
        var empty = box.Succeeded
            ? platform.CallCommand<SetMtuaStatusForced, ProductResponse>(new SetMtuaStatusForced { NId = trailer.StorageMtuaNId, Status = PalletStates.Empty })
            : box;
        if (!empty.Succeeded)
        {
            return YardResponse.From(empty);
        }

        if (Yard.OpenSchedule(platform, trailer.NId) is { } schedule)
        {
            schedule.Status = ScheduleStates.Unloaded;
            schedule.Comments = trailer.ContentType == StorageTypes.Generic
                ? CompleteStorageTrailerLoadHandler.Append(schedule.Comments, command.Comments)
                : schedule.Comments;
            platform.Submit(schedule);
        }

        Yard.SetStatus(platform, trailer, TrailerStates.Empty);
        return YardResponse.Of(trailer);
    }
}

/// <summary>
/// Dỡ một pallet khỏi trailer kho tới một vị trí: tìm pallet trong trailer, tìm vị trí, rồi gọi lệnh chuyển vật chứa của lớp
/// Product. Không đụng tới trailer: dỡ pallet cuối bằng cách này để lại trailer Full với số lượng cũ.
/// </summary>
[Command<YardOperationBlock>]
public sealed class UnloadPalletFromStorageTrailer : ICommand<YardResponse>
{
    [Mandatory]
    public string? Barcode { get; init; }

    [Mandatory]
    public string? PalletNId { get; init; }

    [Mandatory]
    public string? LocationNId { get; init; }
}

internal sealed class UnloadPalletFromStorageTrailerHandler : ICommandHandler<UnloadPalletFromStorageTrailer, YardResponse>
{
    public YardResponse Handle(UnloadPalletFromStorageTrailer command, IPlatform platform)
    {
        if (Yard.Find(platform, command.Barcode!, out var trailer) is { } missing)
        {
            return missing;
        }

        if (!platform.Query<Mtua>().Any(c => c.NId == command.PalletNId && c.ParentMtuaNId == trailer.StorageMtuaNId))
        {
            return YardResponse.Fail(NvErrors.NotInTrailer, $"Pallet {command.PalletNId} is not in trailer {trailer.NId}.");
        }

        if (!platform.ProjectionQuery<Equipment>().Any(e => e.NId == command.LocationNId))
        {
            return YardResponse.Fail(NvErrors.UnknownLocation, $"Location {command.LocationNId} does not exist.");
        }

        var moved = platform.CallCommand<MoveMtua, ProductResponse>(new MoveMtua { NId = command.PalletNId, EquipmentNId = command.LocationNId });
        return moved.Succeeded ? YardResponse.Of(trailer) : YardResponse.From(moved);
    }
}
