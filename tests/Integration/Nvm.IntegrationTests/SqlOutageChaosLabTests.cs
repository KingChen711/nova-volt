using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Nvm.IntegrationTests;

/// <summary>App thật với SQL Server riêng, để lab chaos tắt được database mà không đụng test khác.</summary>
public sealed class DedicatedSqlExecutionFixture : ExecutionCommandHttpFixture
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04").Build();

    protected override async Task<string> CreateSqlDatabaseAsync()
    {
        await _sql.StartAsync(CancellationToken.None);
        await using var connection = new SqlConnection(_sql.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new SqlCommand("CREATE DATABASE chaos;", connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        return new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = "chaos" }.ConnectionString;
    }

    /// <summary>Tạm dừng container (process SQL đứng yên, cổng giữ nguyên): database "tắt" nhìn từ app.</summary>
    public Task PauseSqlAsync() => _sql.PauseAsync(CancellationToken.None);

    public Task ResumeSqlAsync() => _sql.UnpauseAsync(CancellationToken.None);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _sql.DisposeAsync();
    }
}

/// <summary>
/// M13 chaos (T10, N3): SQL Server tắt 2 phút trong lúc thiết bị/người vận hành vẫn gửi và gửi lại; 0 message mất, không
/// nhân bản, app sẵn sàng lại trong &lt; 60 s sau khi SQL trở lại.
/// </summary>
public sealed class SqlOutageChaosLabTests(DedicatedSqlExecutionFixture fixture, ITestOutputHelper output)
    : IClassFixture<DedicatedSqlExecutionFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SqlDownForTwoMinutes_NoSubmissionIsLostOrDuplicated_AndReadinessReturnsWithinSixtySeconds()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NVM_RUN_LABS") == "1",
            "Set NVM_RUN_LABS=1 to run the M13 SQL outage lab (takes about three minutes).");
        var outage = TimeSpan.FromSeconds(double.Parse(Environment.GetEnvironmentVariable("NVM_CHAOS_OUTAGE_SECONDS") ?? "120",
            CultureInfo.InvariantCulture));
        var warm = Enumerable.Range(0, 5).Select(_ => ExecutionCommandHttpFixture.Request()).ToArray();
        foreach (var request in warm)
        { (await SendUntilAcceptedAsync(request, TimeSpan.FromSeconds(30))).ShouldBe(1); }

        await fixture.PauseSqlAsync();
        var down = Stopwatch.StartNew();
        var during = new ConcurrentBag<CollectionRequest>();
        var attempts = new ConcurrentBag<int>();
        var senders = new List<Task>();
        // Mỗi 5 giây trong lúc sập, một submission mới bắt đầu gửi và tự gửi lại (cùng submission) cho tới khi được nhận.
        while (down.Elapsed < outage)
        {
            var request = ExecutionCommandHttpFixture.Request();
            during.Add(request);
            senders.Add(Task.Run(async () => attempts.Add(await SendUntilAcceptedAsync(request, outage + TimeSpan.FromMinutes(3))), Ct));
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        }
        using (var unavailable = await fixture.Client.GetAsync("/health/ready", Ct))
        { unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable); }

        await fixture.ResumeSqlAsync();
        var recovery = Stopwatch.StartNew();
        while (true)
        {
            using var probe = await fixture.Client.GetAsync("/health/ready", Ct);
            if (probe.StatusCode == HttpStatusCode.OK)
            { break; }
            recovery.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "readiness must return within 60 s of SQL returning");
            await Task.Delay(500, Ct);
        }
        var ready = recovery.Elapsed;
        await Task.WhenAll(senders);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CHAOS outage_s={outage.TotalSeconds:F0} submissions={during.Count} ready_after_s={ready.TotalSeconds:F1} all_accepted_after_s={recovery.Elapsed.TotalSeconds:F1} max_attempts={attempts.Max()}"));

        var all = warm.Concat(during).ToArray();
        foreach (var request in all)
        {
            (await fixture.OutcomeCountAsync(request)).ShouldBe(1);            // không nhân bản trong SQL
            (await fixture.EventIntentCountsAsync(request)).ShouldBe([1, 1]);   // đúng một event, một dòng outbox
        }
        // Bus là at-least-once (ADR-040): outbox có thể gửi lại event đã gửi nếu không kịp đánh dấu trong lúc SQL tắt. Không
        // mất nghĩa là mọi ce_id đều tới; bản trùng mang cùng ce_id để consumer bỏ.
        var delivered = new Dictionary<string, int>(StringComparer.Ordinal);
        var wanted = all.Select(r => r.IdempotencyKey.ToString("D")).ToHashSet(StringComparer.Ordinal);
        var waiting = Stopwatch.StartNew();
        while (!wanted.IsSubsetOf(delivered.Keys) && waiting.Elapsed < TimeSpan.FromSeconds(120))
        {
            foreach (var row in await fixture.DrainEventsAsync())
            {
                var id = row.GetProperty("properties").GetProperty("headers").GetProperty("ce_id").GetString()!;
                delivered[id] = delivered.GetValueOrDefault(id) + 1;
            }
            await Task.Delay(500, Ct);
        }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"CHAOS_BUS delivered={wanted.Count(delivered.ContainsKey)}/{wanted.Count} duplicates={delivered.Where(d => wanted.Contains(d.Key)).Sum(d => d.Value - 1)}"));
        wanted.Except(delivered.Keys).ShouldBeEmpty("không event nào được mất trên bus");
        ready.ShouldBeLessThan(TimeSpan.FromSeconds(60));
    }

    /// <summary>Gửi cùng submission tới khi được nhận; trả số lần gửi.</summary>
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
            catch (HttpRequestException)
            { }
            catch (TaskCanceledException) when (!Ct.IsCancellationRequested)
            { }
            if (clock.Elapsed > deadline)
            { throw new TimeoutException($"Submission {request.Payload.SubmissionId} not accepted after {attempt} attempts."); }
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }
    }
}
