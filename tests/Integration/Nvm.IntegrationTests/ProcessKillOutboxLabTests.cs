using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace Nvm.IntegrationTests;

/// <summary>
/// Lab M6 (T3): giết process app (<c>Process.Kill</c> = TerminateProcess trên Windows, SIGKILL trên Linux) nhiều lần giữa lúc
/// có traffic ghi liên tục, nên một số lần kill rơi vào giữa commit hoặc giữa claim outbox và publish. Sau khi khởi động
/// lại: mỗi submission đúng một outcome, một event, một dòng outbox trong SQL; mọi <c>ce_id</c> đều tới bus. Bản trùng
/// trên bus được đếm, không bị cấm: bus là at-least-once và bản trùng mang cùng <c>ce_id</c> (ADR-040).
/// </summary>
public sealed class ProcessKillOutboxLabTests(ExecutionCommandHttpFixture fixture, ITestOutputHelper output)
    : IClassFixture<ExecutionCommandHttpFixture>
{
    private const int Senders = 8;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KillNineDuringCommits_NothingLost_OneEventPerSubmission_EveryCeIdDelivered()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M6 kill -9 outbox lab (takes a few minutes).");
        var kills = int.TryParse(Environment.GetEnvironmentVariable("NVM_LAB_KILLS"), out var k) ? k : 10;
        var submitted = new ConcurrentBag<CollectionRequest>();
        var attempts = new ConcurrentBag<int>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var senders = Enumerable.Range(0, Senders).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var request = ExecutionCommandHttpFixture.Request();
                submitted.Add(request);
                attempts.Add(await SendUntilAcceptedAsync(request, TimeSpan.FromMinutes(3)));
            }
        }, CancellationToken.None)).ToArray();

        var random = new Random(20260927);
        var restarts = new List<double>();
        for (var i = 0; i < kills; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500 + random.Next(1500)), Ct);
            var down = Stopwatch.StartNew();
            await fixture.RestartAppAsync();   // StopAppAsync = Process.Kill(entireProcessTree: true)
            restarts.Add(down.Elapsed.TotalSeconds);
        }
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await stop.CancelAsync();
        await Task.WhenAll(senders);

        var all = submitted.ToArray();
        var extraOutcomes = 0;
        var badIntents = 0;
        foreach (var request in all)
        {
            if (await fixture.OutcomeCountAsync(request) != 1)
            { extraOutcomes++; }
            if (!(await fixture.EventIntentCountsAsync(request)).SequenceEqual([1, 1]))
            { badIntents++; }
        }

        // Kill giữa claim và publish thì dòng outbox chỉ được claim lại sau khi lease 120 s hết hạn.
        var delivered = new Dictionary<string, int>(StringComparer.Ordinal);
        var wanted = all.Select(r => r.IdempotencyKey.ToString("D")).ToHashSet(StringComparer.Ordinal);
        var waiting = Stopwatch.StartNew();
        while (!wanted.IsSubsetOf(delivered.Keys) && waiting.Elapsed < TimeSpan.FromSeconds(200))
        {
            foreach (var row in await fixture.DrainEventsAsync())
            {
                var id = row.GetProperty("properties").GetProperty("headers").GetProperty("ce_id").GetString()!;
                delivered[id] = delivered.GetValueOrDefault(id) + 1;
            }
            await Task.Delay(500, Ct);
        }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"KILL9 kills={kills} submissions={all.Length} max_attempts={attempts.Max()} restart_s_max={restarts.Max():F1} sql_extra_outcomes={extraOutcomes} sql_bad_event_or_outbox={badIntents} delivered={wanted.Count(delivered.ContainsKey)}/{wanted.Count} bus_duplicates={delivered.Where(d => wanted.Contains(d.Key)).Sum(d => d.Value - 1)} all_delivered_after_s={waiting.Elapsed.TotalSeconds:F1}"));
        extraOutcomes.ShouldBe(0);
        badIntents.ShouldBe(0);
        wanted.Except(delivered.Keys).ShouldBeEmpty("kill -9 không được làm mất event");
    }

    private async Task<int> SendUntilAcceptedAsync(CollectionRequest request, TimeSpan deadline)
    {
        var clock = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await fixture.SendAsync(request, fixture.Token());
                if (response.StatusCode == HttpStatusCode.OK)
                { return attempt; }
            }
            // App đang chết/khởi động lại: kết nối bị từ chối, bị cắt, hoặc HttpClient cũ đã dispose.
            catch (Exception error) when (error is HttpRequestException or ObjectDisposedException or NullReferenceException
                                           or TaskCanceledException && !Ct.IsCancellationRequested)
            { }
            if (clock.Elapsed > deadline)
            { throw new TimeoutException($"Submission {request.Payload.SubmissionId} not accepted after {attempt} attempts."); }
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }
}
