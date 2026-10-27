namespace Nvm.Foundation;

/// <summary>Đổi trạng thái theo state machine dạng dữ liệu: transition và role nạp bằng command, không cần code.</summary>
public static class StateMachines
{
    public static string TransitionNId(string machine, string? fromStatus, string toStatus) =>
        $"{machine}:{fromStatus ?? string.Empty}->{toStatus}";

    /// <summary>
    /// Đổi <see cref="IHasStatus.Status"/> của <paramref name="item"/> sang <paramref name="toStatus"/> rồi <c>Submit</c>.
    /// Transition phải có trong dữ liệu; nếu transition có gán role thì người gọi phải có ít nhất một role trong đó.
    /// </summary>
    /// <exception cref="StatusTransitionException">Không có transition, hoặc người gọi thiếu role.</exception>
    public static void ChangeStatus<T>(this IPlatform platform, T item, string machine, string toStatus)
        where T : ModelObject, IHasStatus
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(item);
        var nid = TransitionNId(machine, item.Status, toStatus);

        // Dữ liệu cấu hình thuộc domain ReferenceData: đọc ngoài transaction, chỉ bản đã commit.
        if (!platform.ProjectionQuery<StatusTransition>().Any(t => t.NId == nid))
        {
            throw new StatusTransitionException(7101, $"{machine}: no transition from '{item.Status}' to '{toStatus}'.");
        }

        var roles = platform.ProjectionQuery<TransitionRole>().Where(r => r.TransitionNId == nid).Select(r => r.Role!).ToList();
        if (roles.Count > 0 && !roles.Any(platform.Caller.IsInRole))
        {
            throw new StatusTransitionException(7102,
                $"{machine}: transition '{item.Status}' -> '{toStatus}' requires one of the roles {string.Join(", ", roles)}.");
        }

        item.Status = toStatus;
        platform.Submit(item);
        platform.Tracer.Write(TraceLevel.Verbose, $"{machine} {item.Id}: {nid} by {platform.Caller.User}");
    }
}

/// <summary>Transition không có trong dữ liệu, hoặc người gọi không có role được gán cho nó.</summary>
public sealed class StatusTransitionException(int errorCode, string message) : InvalidOperationException(message)
{
    public int ErrorCode { get; } = errorCode;
}
