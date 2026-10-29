using Nvm.Foundation;

namespace Nvm.FoundationTests.LabModel;

// App, extension app, state machine, signal rule, event subscriber và reading function của model thử nghiệm (R2).

/// <summary>Base app: import mọi entity, facet, command của FB thử nghiệm và một reading function.</summary>
[App(LabApp.Name)]
public sealed class LabAppDefinition : AppDefinition
{
    protected override void Define(PomBuilder pom) => pom
        .Entity<LabMaterial>().Entity<LabOrder>().Entity<LabOrderNote>().Entity<LabOrderCounters>().Entity<LabOrderSupplier>()
        .Entity<LabTick>().Entity<LabOrderSummary>()
        .Command<CreateLabMaterial>().Command<CreateLabOrder>().Command<DeleteLabOrder>().Command<AddLabOrderNote>()
        .Command<CreateLabOrderWithNote>().Command<CreateLabOrderWithMaterial>().Command<SetLabOrderSupplier>()
        .Command<CreateAndCountLabOrders>().Command<ReadMaterialWithQuery>().Command<SetLabOrderStatus>()
        .Command<RecordLabTick>()
        .ReadingFunction<OrdersOfMaterial>().ReadingFunction<FailingOrders>();
}

/// <summary>App thứ hai: cùng command, nhưng không bật pre-check nào; không import <see cref="LabOrderNote"/>.</summary>
[App(LabApp.OtherName)]
public sealed class OtherAppDefinition : AppDefinition
{
    protected override void Define(PomBuilder pom) => pom
        .Entity<LabMaterial>().Entity<LabOrder>()
        .Command<CreateLabMaterial>().Command<CreateLabOrder>().Command<DeleteLabOrder>();
}

/// <summary>Extension app của LabApp: command của nó gọi được qua LabApp với tiền tố <c>LabExt_</c>.</summary>
[App(LabApp.ExtensionName, BaseApp = typeof(LabAppDefinition))]
public sealed class LabExtensionAppDefinition : AppDefinition
{
    protected override void Define(PomBuilder pom) => pom.Command<FlagLabOrder>();
}

/// <summary>Command của FB "của team", import vào POM của extension app: công bố trên LabApp là <c>LabExt_FlagLabOrder</c>.</summary>
[Command<LabExtensionBlock>]
public sealed class FlagLabOrder : ICommand<LabResponse>
{
    [Mandatory]
    public string? OrderNId { get; init; }
}

internal sealed class FlagLabOrderHandler : ICommandHandler<FlagLabOrder, LabResponse>
{
    public LabResponse Handle(FlagLabOrder command, IPlatform platform)
    {
        var note = platform.Create<LabOrderNote>();
        note.NId = note.Id.ToString();
        note.OrderNId = command.OrderNId;
        note.Text = "flagged";
        platform.Submit(note);
        return new LabResponse();
    }
}

[Entity<LabOperationBlock>]
public sealed class LabTick : Entity
{
    public string? Source { get; set; }
}

/// <summary>Command đếm được: mỗi lần chạy thêm một dòng. Signal rule gọi nó.</summary>
[Command<LabOperationBlock>]
public sealed class RecordLabTick : ICommand<LabResponse>
{
    public string? Source { get; init; }
}

public sealed class RecordLabTickHandler : ICommandHandler<RecordLabTick, LabResponse>
{
    public LabResponse Handle(RecordLabTick command, IPlatform platform)
    {
        var tick = platform.Create<LabTick>();
        tick.NId = tick.Id.ToString();
        tick.Source = command.Source;
        platform.Submit(tick);
        return new LabResponse();
    }
}

[Command<LabOperationBlock>]
public sealed class SetLabOrderStatus : ICommand<LabResponse>
{
    [Mandatory]
    public string? OrderNId { get; init; }

    [Mandatory]
    public string? Status { get; init; }
}

public sealed class SetLabOrderStatusHandler : ICommandHandler<SetLabOrderStatus, LabResponse>
{
    public LabResponse Handle(SetLabOrderStatus command, IPlatform platform)
    {
        var order = platform.Query<LabOrder>().Single(o => o.NId == command.OrderNId);
        platform.ChangeStatus(order, "LabOrder", command.Status!);
        return new LabResponse();
    }
}

/// <summary>Composite của extension app: đánh dấu order bằng một note.</summary>
[AppCommand(LabApp.ExtensionName)]
public sealed class TagLabOrder : ICommand<LabResponse>
{
    [Mandatory]
    public string? OrderNId { get; init; }
}

public sealed class TagLabOrderHandler : ICompositeCommandHandler<TagLabOrder, LabResponse>
{
    public LabResponse Handle(TagLabOrder command, ICompositePlatform platform) =>
        platform.CallCommand<AddLabOrderNote, LabResponse>(new AddLabOrderNote { OrderNId = command.OrderNId, Text = "tagged" });
}

/// <summary>Predefined: bật ở mọi plant mà không cần dữ liệu.</summary>
[EventSubscriber<LabOperationBlock>(Subscription = "LabOrderAudit", Predefined = true, WorkerRole = "Audit")]
public sealed class LabOrderAuditor : IEventSubscriber<LabOrderCreated>
{
    public void Handle(LabOrderCreated domainEvent, IPlatform platform)
    {
        var tick = platform.Create<LabTick>();
        tick.NId = tick.Id.ToString();
        tick.Source = "audit:" + domainEvent.NId;
        platform.Submit(tick);
    }
}

/// <summary>Không Predefined: chỉ chạy khi plant tạo subscription <c>LabOrderNotify</c>.</summary>
[EventSubscriber<LabOperationBlock>(Subscription = "LabOrderNotify")]
public sealed class LabOrderNotifier : IEventSubscriber<LabOrderCreated>
{
    /// <summary>Test giữ handler lại để chứng minh nó chạy sau khi response đã trả.</summary>
    public static SemaphoreSlim Gate { get; } = new(0);

    public void Handle(LabOrderCreated domainEvent, IPlatform platform)
    {
        Gate.Wait(TimeSpan.FromSeconds(30));
        var tick = platform.Create<LabTick>();
        tick.NId = tick.Id.ToString();
        tick.Source = "notify:" + domainEvent.NId;
        platform.Submit(tick);
    }
}

/// <summary>Reading model: view <c>dbo.LabOrderSummary</c> phải cài tay, Update Database không tạo nó.</summary>
[ReadingModel<LabOperationBlock>]
public sealed class LabOrderSummary
{
    public string? OrderNId { get; set; }

    public string? MaterialNId { get; set; }

    public decimal? Quantity { get; set; }
}

public sealed class OrdersOfMaterialParameters
{
    public string? MaterialNId { get; init; }
}

[ReadingFunction(LabApp.Name)]
public sealed class OrdersOfMaterial : IReadingFunction<OrdersOfMaterialParameters, LabOrderSummary>
{
    public IQueryable<LabOrderSummary> Read(OrdersOfMaterialParameters parameters, IReadingPlatform platform) =>
        platform.ProjectionQuery<LabOrderSummary>().Where(s => s.MaterialNId == parameters.MaterialNId);
}

/// <summary>Reading function ném lỗi ngay lúc gọi, như code của team gặp configuration key thiếu (lab L18).</summary>
[ReadingFunction(LabApp.Name)]
public sealed class FailingOrders : IReadingFunction<OrdersOfMaterialParameters, LabOrderSummary>
{
    public IQueryable<LabOrderSummary> Read(OrdersOfMaterialParameters parameters, IReadingPlatform platform) =>
        throw new InvalidOperationException("Configuration key Lab.Connection is missing.");
}
