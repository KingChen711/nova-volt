namespace Nvm.Foundation;

// Dữ liệu cấu hình của nền: nạp bằng command (về sau bằng suite master data), đọc bằng ProjectionQuery.

[FunctionalBlock("FB_RF_Foundation", ModelDomain.ReferenceData)]
public sealed class FoundationReferenceBlock;

[FunctionalBlock("FB_OP_Foundation", ModelDomain.OperationalData)]
public sealed class FoundationOperationBlock;

/// <summary>Configuration key: giá trị luôn là chuỗi; mỗi chỗ đọc tự quyết khi thiếu key (bỏ qua, mặc định, hay báo lỗi).</summary>
[Entity<FoundationReferenceBlock>]
public sealed class ConfigurationKey : Entity
{
    [TextLength(-1)]
    public string? Val { get; set; }

    public string? Type { get; set; }

    public string? Category { get; set; }
}

/// <summary>Một trạng thái của state machine. NId = <c>&lt;Machine&gt;.&lt;Status&gt;</c>.</summary>
[Entity<FoundationReferenceBlock>]
public sealed class StatusDefinition : Entity
{
    [Mandatory]
    public string? Machine { get; set; }

    [Mandatory]
    public string? Status { get; set; }
}

/// <summary>Một transition được phép. NId = <c>&lt;Machine&gt;:&lt;From&gt;-&gt;&lt;To&gt;</c>; <c>From</c> rỗng là trạng thái đầu.</summary>
[Entity<FoundationReferenceBlock>]
public sealed class StatusTransition : Entity
{
    [Mandatory]
    public string? Machine { get; set; }

    public string? FromStatus { get; set; }

    [Mandatory]
    public string? ToStatus { get; set; }
}

/// <summary>Role được đi một transition. Transition không có dòng nào thì ai cũng đi được.</summary>
[Entity<FoundationReferenceBlock>]
public sealed class TransitionRole : Entity
{
    [Mandatory]
    public string? TransitionNId { get; set; }

    [Mandatory]
    public string? Role { get; set; }
}

/// <summary>Luật gọi một command khi timer tới hạn hoặc khi một event được phát.</summary>
/// <remarks><see cref="Parameters"/> là JSON của command; với luật theo event, <c>{Tên}</c> được thay bằng thuộc tính của event.</remarks>
[Entity<FoundationReferenceBlock>]
public sealed class SignalRule : Entity
{
    /// <summary><c>Timer</c> hoặc <c>Event</c>.</summary>
    [Mandatory]
    public string? Trigger { get; set; }

    public string? TimerNId { get; set; }

    /// <summary>Tên đầy đủ của event, vd <c>FB_OP_Lab.LabOrderCreated</c>.</summary>
    public string? EventType { get; set; }

    [Mandatory]
    public string? App { get; set; }

    [Mandatory]
    public string? Command { get; set; }

    [TextLength(-1)]
    public string? Parameters { get; set; }

    public bool Enabled { get; set; }
}

/// <summary>Subscription tạo theo plant: bật handler đã khai trong model mà không Predefined.</summary>
/// <remarks>NId là tên subscription của handler. Có hiệu lực khi worker khởi động lại, như cấu hình của nền thật.</remarks>
[Entity<FoundationReferenceBlock>]
public sealed class EventSubscription : Entity
{
    public bool Enabled { get; set; }
}

/// <summary>Timer tạo lúc chạy, có mã định danh (vd timer đầu ca). Tới hạn thì các signal rule trỏ vào nó chạy.</summary>
[Entity<FoundationOperationBlock>]
public sealed class SystemTimer : Entity
{
    public DateTimeOffset? NextDueOn { get; set; }

    /// <summary>0 là timer một lần: chạy xong thì tắt.</summary>
    public int IntervalSeconds { get; set; }

    public bool Enabled { get; set; }
}

[App("Foundation")]
public sealed class FoundationApp : AppDefinition
{
    protected override void Define(PomBuilder pom) => pom
        .Entity<ConfigurationKey>().Entity<StatusDefinition>().Entity<StatusTransition>().Entity<TransitionRole>()
        .Entity<SignalRule>().Entity<EventSubscription>().Entity<SystemTimer>()
        .Command<SetConfigurationKey>().Command<DefineStatus>().Command<DefineTransition>().Command<AssignTransitionRole>()
        .Command<DefineSignalRule>().Command<SetEventSubscription>().Command<CreateTimer>().Command<DeleteTimer>();
}

public sealed class FoundationResponse : CommandResponse;
