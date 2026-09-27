using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Nvm.Kernel.Commands;

/// <summary>
/// Nguồn trace và metric của kernel, chỉ dùng BCL (<see cref="ActivitySource"/>, <see cref="Meter"/>): kernel không phụ
/// thuộc OpenTelemetry (K9). Host nào bật OpenTelemetry thì đăng ký tên <see cref="Name"/>.
/// </summary>
public static class KernelTelemetry
{
    public const string Name = "NovaVolt.Kernel";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    private static readonly Counter<long> Commands = Meter.CreateCounter<long>("nvm.commands",
        description: "Command đã xử lý, theo loại và kết quả (accepted, mã lý do từ chối, error).");

    public static void RecordCommand(string commandType, string outcome) =>
        Commands.Add(1, new KeyValuePair<string, object?>("command", commandType), new KeyValuePair<string, object?>("outcome", outcome));
}
