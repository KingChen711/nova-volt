using System.Diagnostics;

namespace Nvm.IntegrationTests;

public sealed class CommandHostStartupTests
{
    [Theory]
    [InlineData("Nvm.App.Execution")]
    [InlineData("Nvm.Host.All")]
    public async Task ProductionProcessWithoutDurableStoreConfiguration_RefusesStartup(string app)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var dll = Path.Combine(directory.Parent!.Parent!.FullName, app, directory.Name, app + ".dll");
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(dll);
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        info.Environment.Remove("NVM_COMMANDS__ConnectionString");
        info.Environment.Remove("NVM_COMMANDS:ConnectionString");
        using var process = Process.Start(info)!;
        var ct = TestContext.Current.CancellationToken;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(20), ct);
            process.ExitCode.ShouldNotBe(0);
            ((await output) + (await error)).ShouldContain("in-memory command storage is forbidden");
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(entireProcessTree: true); }
        }
    }
}
