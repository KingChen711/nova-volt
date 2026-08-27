namespace Nvm.Hosting;

/// <summary>The two health check tags this system uses, and the line between them.</summary>
/// <remarks>
/// <para>
/// Liveness and readiness answer different questions and a probe belongs to exactly one of them:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>live</b> — "the process is alive, do not restart me". Nothing that reaches outside the
///     process may appear here. A dependency being down is not a reason to kill a healthy service,
///     and an orchestrator that restarts it makes an outage worse by adding cold starts to it.
///   </description></item>
///   <item><description>
///     <b>ready</b> — "my dependencies are reachable, send me traffic". This is where an outage
///     belongs: the instance steps out of rotation and comes back on its own when the dependency
///     returns.
///   </description></item>
/// </list>
/// <para>
/// Stated here rather than typed at each registration because the tag is the whole routing
/// mechanism: <c>MapHealthChecks</c> filters on it, and a probe tagged <c>"redy"</c> silently
/// disappears from both endpoints. Nothing warns, and the check is simply never run again.
/// </para>
/// </remarks>
public static class HealthTags
{
    /// <summary>The process itself is running. In-process checks only.</summary>
    public const string Live = "live";

    /// <summary>This instance can serve traffic. Where dependency checks go.</summary>
    public const string Ready = "ready";
}
