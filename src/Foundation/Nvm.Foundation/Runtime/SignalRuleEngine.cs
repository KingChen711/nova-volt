using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Nvm.Foundation.Runtime;

/// <summary>Chạy signal rule: theo timer tới hạn, hoặc theo event được phát. Mỗi lần chạy là một lời gọi command qua app của rule.</summary>
/// <remarks>Đồng hồ là <see cref="TimeProvider"/> của nền (K1), nên test tua giờ bằng <c>FakeTimeProvider</c>.</remarks>
public sealed partial class SignalRuleEngine(CommandExecutor executor, string connectionString)
{
    /// <summary>Chạy mọi timer đã tới hạn, kể cả các nhịp bị lỡ: mỗi nhịp một lần chạy rule. Trả số lời gọi command.</summary>
    public int RunDueTimers()
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var context = FoundationDbContext.Create(connection, executor.Registry, executor.Time, tracking: true);
        var now = executor.Time.GetUtcNow();
        var fired = 0;
        foreach (var timer in context.Set<SystemTimer>().Where(t => t.Enabled && t.NextDueOn <= now).ToList())
        {
            var rules = context.Set<SignalRule>()
                .Where(r => r.Enabled && r.Trigger == "Timer" && r.TimerNId == timer.NId)
                .ToList();
            while (timer.Enabled && timer.NextDueOn <= now)
            {
                foreach (var rule in rules)
                {
                    Fire(rule, rule.Parameters, correlationId: null);
                    fired++;
                }

                if (timer.IntervalSeconds > 0)
                {
                    timer.NextDueOn = timer.NextDueOn!.Value.AddSeconds(timer.IntervalSeconds);
                }
                else
                {
                    timer.Enabled = false;
                }
            }

            context.SaveChanges();
        }

        return fired;
    }

    /// <summary>Chạy các rule bật cho <paramref name="eventType"/>. <c>{Tên}</c> trong tham số lấy từ thuộc tính của event.</summary>
    public int RunEventRules(string eventType, string payload, Guid correlationId)
    {
        using var read = executor.OpenRead();
        var rules = read.SetOf<SignalRule>().Where(r => r.Enabled && r.Trigger == "Event" && r.EventType == eventType).ToList();
        if (rules.Count == 0)
        {
            return 0;
        }

        using var document = JsonDocument.Parse(payload);
        foreach (var rule in rules)
        {
            var parameters = Placeholder().Replace(rule.Parameters ?? "{}", match =>
                document.RootElement.TryGetProperty(match.Groups[1].Value, out var value)
                    ? value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
                    : match.Value);
            Fire(rule, parameters, correlationId);
        }

        return rules.Count;
    }

    /// <summary>Vòng lặp của host: mỗi nhịp chạy timer tới hạn.</summary>
    public async Task RunAsync(TimeSpan tick, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RunDueTimers();
            await Task.Delay(tick, executor.Time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Fire(SignalRule rule, string? parameters, Guid? correlationId)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(parameters) ? "{}" : parameters);
        if (executor.Registry.HasApp(rule.App!) && executor.Registry.CommandByPublicName(rule.App!, rule.Command!) is { } model)
        {
            var command = document.RootElement.Deserialize(model.Type, CommandExecutor.RequestJson)!;
            executor.Execute(rule.App!, command, CallerIdentity.System, correlationId);
        }
        else
        {
            // Rule trỏ tới command mà app không công bố: lời gọi trả 404, các rule khác vẫn chạy.
            executor.Execute(rule.App!, rule.Command!, document.RootElement, CallerIdentity.System);
        }
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();
}
