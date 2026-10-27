namespace Nvm.Foundation;

// Command nạp dữ liệu cấu hình của nền. Đều "tạo hoặc sửa theo NId" để suite master data chạy lại được.

[Command<FoundationReferenceBlock>]
public sealed class SetConfigurationKey : ICommand<FoundationResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    public string? Val { get; init; }

    public string? Type { get; init; }

    public string? Category { get; init; }
}

internal sealed class SetConfigurationKeyHandler : ICommandHandler<SetConfigurationKey, FoundationResponse>
{
    public FoundationResponse Handle(SetConfigurationKey command, IPlatform platform)
    {
        var key = platform.Query<ConfigurationKey>().SingleOrDefault(k => k.NId == command.NId) ?? platform.Create<ConfigurationKey>();
        key.NId = command.NId!;
        key.Val = command.Val;
        key.Type = command.Type;
        key.Category = command.Category;
        platform.Submit(key);
        return new FoundationResponse();
    }
}

[Command<FoundationReferenceBlock>]
public sealed class DefineStatus : ICommand<FoundationResponse>
{
    [Mandatory]
    public string? Machine { get; init; }

    [Mandatory]
    public string? Status { get; init; }
}

internal sealed class DefineStatusHandler : ICommandHandler<DefineStatus, FoundationResponse>
{
    public FoundationResponse Handle(DefineStatus command, IPlatform platform)
    {
        var nid = command.Machine + "." + command.Status;
        if (!platform.Query<StatusDefinition>().Any(s => s.NId == nid))
        {
            var status = platform.Create<StatusDefinition>();
            status.NId = nid;
            status.Machine = command.Machine;
            status.Status = command.Status;
            platform.Submit(status);
        }

        return new FoundationResponse();
    }
}

[Command<FoundationReferenceBlock>]
public sealed class DefineTransition : ICommand<FoundationResponse>
{
    [Mandatory]
    public string? Machine { get; init; }

    /// <summary>Rỗng: trạng thái đầu khi tạo đối tượng.</summary>
    public string? FromStatus { get; init; }

    [Mandatory]
    public string? ToStatus { get; init; }
}

internal sealed class DefineTransitionHandler : ICommandHandler<DefineTransition, FoundationResponse>
{
    public FoundationResponse Handle(DefineTransition command, IPlatform platform)
    {
        var response = new FoundationResponse();
        var known = platform.Query<StatusDefinition>().Where(s => s.Machine == command.Machine).Select(s => s.Status).ToList();
        foreach (var status in new[] { command.FromStatus, command.ToStatus }.Where(s => !string.IsNullOrEmpty(s)))
        {
            if (!known.Contains(status))
            {
                response.SetError(7001, $"Status {status} is not defined for {command.Machine}.");
                return response;
            }
        }

        var nid = StateMachines.TransitionNId(command.Machine!, command.FromStatus, command.ToStatus!);
        if (!platform.Query<StatusTransition>().Any(t => t.NId == nid))
        {
            var transition = platform.Create<StatusTransition>();
            transition.NId = nid;
            transition.Machine = command.Machine;
            transition.FromStatus = command.FromStatus ?? string.Empty;
            transition.ToStatus = command.ToStatus;
            platform.Submit(transition);
        }

        return response;
    }
}

/// <summary>Giới hạn transition cho một role. Không kiểm role có tồn tại ở nhà cung cấp danh tính không (lab L06).</summary>
[Command<FoundationReferenceBlock>]
public sealed class AssignTransitionRole : ICommand<FoundationResponse>
{
    [Mandatory]
    public string? Machine { get; init; }

    public string? FromStatus { get; init; }

    [Mandatory]
    public string? ToStatus { get; init; }

    [Mandatory]
    public string? Role { get; init; }
}

internal sealed class AssignTransitionRoleHandler : ICommandHandler<AssignTransitionRole, FoundationResponse>
{
    public FoundationResponse Handle(AssignTransitionRole command, IPlatform platform)
    {
        var response = new FoundationResponse();
        var transition = StateMachines.TransitionNId(command.Machine!, command.FromStatus, command.ToStatus!);
        if (!platform.Query<StatusTransition>().Any(t => t.NId == transition))
        {
            response.SetError(7002, $"Transition {transition} is not defined.");
            return response;
        }

        var nid = transition + "#" + command.Role;
        if (!platform.Query<TransitionRole>().Any(r => r.NId == nid))
        {
            var role = platform.Create<TransitionRole>();
            role.NId = nid;
            role.TransitionNId = transition;
            role.Role = command.Role;
            platform.Submit(role);
        }

        return response;
    }
}

[Command<FoundationReferenceBlock>]
public sealed class DefineSignalRule : ICommand<FoundationResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public string? Trigger { get; init; }

    public string? TimerNId { get; init; }

    public string? EventType { get; init; }

    [Mandatory]
    public string? App { get; init; }

    [Mandatory]
    public string? Command { get; init; }

    public string? Parameters { get; init; }

    public bool Enabled { get; init; } = true;
}

internal sealed class DefineSignalRuleHandler : ICommandHandler<DefineSignalRule, FoundationResponse>
{
    public FoundationResponse Handle(DefineSignalRule command, IPlatform platform)
    {
        var response = new FoundationResponse();
        if (command.Trigger is not ("Timer" or "Event"))
        {
            response.SetError(7003, "Trigger must be Timer or Event.");
            return response;
        }

        var rule = platform.Query<SignalRule>().SingleOrDefault(r => r.NId == command.NId) ?? platform.Create<SignalRule>();
        rule.NId = command.NId!;
        rule.Trigger = command.Trigger;
        rule.TimerNId = command.TimerNId;
        rule.EventType = command.EventType;
        rule.App = command.App;
        rule.Command = command.Command;
        rule.Parameters = command.Parameters;
        rule.Enabled = command.Enabled;
        platform.Submit(rule);
        return response;
    }
}

[Command<FoundationReferenceBlock>]
public sealed class SetEventSubscription : ICommand<FoundationResponse>
{
    /// <summary>Tên subscription mà handler khai trong model.</summary>
    [Mandatory]
    public string? NId { get; init; }

    public bool Enabled { get; init; } = true;
}

internal sealed class SetEventSubscriptionHandler : ICommandHandler<SetEventSubscription, FoundationResponse>
{
    public FoundationResponse Handle(SetEventSubscription command, IPlatform platform)
    {
        var subscription = platform.Query<EventSubscription>().SingleOrDefault(s => s.NId == command.NId)
            ?? platform.Create<EventSubscription>();
        subscription.NId = command.NId!;
        subscription.Enabled = command.Enabled;
        platform.Submit(subscription);
        return new FoundationResponse();
    }
}

[Command<FoundationOperationBlock>]
public sealed class CreateTimer : ICommand<FoundationResponse>
{
    [Mandatory]
    public string? NId { get; init; }

    [Mandatory]
    public DateTimeOffset? FirstDueOn { get; init; }

    public int IntervalSeconds { get; init; }
}

internal sealed class CreateTimerHandler : ICommandHandler<CreateTimer, FoundationResponse>
{
    public FoundationResponse Handle(CreateTimer command, IPlatform platform)
    {
        var timer = platform.Query<SystemTimer>().SingleOrDefault(t => t.NId == command.NId) ?? platform.Create<SystemTimer>();
        timer.NId = command.NId!;
        timer.NextDueOn = command.FirstDueOn;
        timer.IntervalSeconds = command.IntervalSeconds;
        timer.Enabled = true;
        platform.Submit(timer);
        return new FoundationResponse();
    }
}

[Command<FoundationOperationBlock>]
public sealed class DeleteTimer : ICommand<FoundationResponse>
{
    [Mandatory]
    public string? NId { get; init; }
}

internal sealed class DeleteTimerHandler : ICommandHandler<DeleteTimer, FoundationResponse>
{
    public FoundationResponse Handle(DeleteTimer command, IPlatform platform)
    {
        if (platform.Query<SystemTimer>().SingleOrDefault(t => t.NId == command.NId) is { } timer)
        {
            platform.Delete(timer);
        }

        return new FoundationResponse();
    }
}
