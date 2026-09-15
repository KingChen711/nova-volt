using System.Diagnostics;
using System.Reflection;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Idempotency;

// Oracle độc lập chỉ gọi API đã có trên parent C04; không chép implementation C05 vào parent.
if (args[0] == "worker")
{
    var store = new InMemoryIdempotencyStore(TimeProvider.System);
    var key = IdempotencyKey.FromNaturalKey("NV1", "parent-restart-proof");
    var claim = await store.ClaimAsync<int>(key, "LegacyWrite", CancellationToken.None);
    if (claim.IsGranted)
    {
        // File là effect bền vững nhỏ cho oracle; worker mới vẫn nhìn thấy nó sau khi process cũ chết.
        await File.AppendAllTextAsync(args[1], "effect\n");
        await store.CompleteAsync(key, 1, TimeProvider.System.GetUtcNow(), CancellationToken.None);
    }
    return 0;
}

var effectPath = Path.GetFullPath(args[0]);
if (File.Exists(effectPath)) { throw new InvalidOperationException("Use a fresh artifact path."); }
for (var i = 0; i < 2; i++)
{
    var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
    info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add("worker");
    info.ArgumentList.Add(effectPath);
    using var child = Process.Start(info)!;
    await child.WaitForExitAsync();
    if (child.ExitCode != 0) { return 2; }
}
var observed = (await File.ReadAllLinesAsync(effectPath)).Length;
Console.WriteLine($"Expected durable effects after two processes: 1; observed: {observed}");
return observed == 1 ? 0 : 1;
