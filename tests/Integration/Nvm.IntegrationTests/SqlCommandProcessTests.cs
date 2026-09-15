using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.Kernel.Commands;

namespace Nvm.IntegrationTests;

public sealed class SqlCommandProcessTests(SqlCommandStoreFixture fixture) : IClassFixture<SqlCommandStoreFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SeparateProcesses_ReplayCommittedOutcome_WithoutSecondEffect()
    {
        var first = await RunAsync("commit", "process-replay");
        var second = await RunAsync("commit", "process-replay");
        first.ProcessId.ShouldNotBe(second.ProcessId);
        first.Handled.ShouldBeTrue();
        second.Handled.ShouldBeFalse();
        second.Result.ShouldBe(first.Result);
        (await CountAsync("process-replay", outcomes: false)).ShouldBe(1);
        (await CountAsync("process-replay", outcomes: true)).ShouldBe(1);
    }

    [Fact]
    public async Task ProcessKilledAfterEffectBeforeCommit_RollsBackThenRetryCommitsOnce()
    {
        using var process = Start("crash", "process-crash");
        try
        {
            (await process.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct))
                .ShouldBe("effect-written");
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(Ct);
        }
        (await CountAsync("process-crash", outcomes: false)).ShouldBe(0);
        (await CountAsync("process-crash", outcomes: true)).ShouldBe(0);
        (await RunAsync("commit", "process-crash")).Handled.ShouldBeTrue();
        (await RunAsync("commit", "process-crash")).Handled.ShouldBeFalse();
        (await CountAsync("process-crash", outcomes: false)).ShouldBe(1);
    }

    [Fact]
    public async Task InMemoryControl_InTwoProcesses_RunsHandlerTwice()
    {
        (await RunAsync("legacy", "legacy-process")).Handled.ShouldBeTrue();
        (await RunAsync("legacy", "legacy-process")).Handled.ShouldBeTrue();
    }

    private Process Start(string mode, string submission)
    {
        var testDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var probe = Path.Combine(testDirectory.Parent!.Parent!.FullName, "Nvm.CommandStoreProbe", testDirectory.Name, "Nvm.CommandStoreProbe.dll");
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(probe);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(submission);
        info.Environment["NVM_PROBE_SQL"] = fixture.ConnectionString;
        return Process.Start(info)!;
    }

    private async Task<ProbeOutput> RunAsync(string mode, string submission)
    {
        using var process = Start(mode, submission);
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        try
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(40), Ct);
            process.ExitCode.ShouldBe(0, await stderr);
            return JsonSerializer.Deserialize<ProbeOutput>(await stdout)!;
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(entireProcessTree: true); }
        }
    }

    private async Task<int> CountAsync(string submission, bool outcomes)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        using var count = connection.CreateCommand();
        if (outcomes)
        {
            count.CommandText = "SELECT COUNT(*) FROM command_store.CommandOutcomes WHERE SiteId='NV1' AND IdempotencyKey=@key";
        }
        else
        {
            count.CommandText = "SELECT COUNT(*) FROM execution.DataCollectionTest WHERE SiteId='NV1' AND IdempotencyKey=@key";
        }
        count.Parameters.AddWithValue("@key", IdempotencyKey.FromNaturalKey("NV1", "ProbeCommand", submission).Value);
        return (int)(await count.ExecuteScalarAsync(Ct))!;
    }

    private sealed record ProbeOutput(int Result, bool Handled, int ProcessId);
}
