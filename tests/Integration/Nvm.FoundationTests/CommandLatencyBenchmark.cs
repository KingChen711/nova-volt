using System.Diagnostics;
using System.Globalization;
using Nvm.FoundationTests.LabModel;

namespace Nvm.FoundationTests;

/// <summary>DoD R1 ý 9: 1.000 command ghi tuần tự qua đủ pipeline (pre-check, handler, 2 post-action, commit, event).</summary>
/// <remarks>Số đo ghi vào docs/benchmarks.md kèm ngày và commit. Ngưỡng của assert chốt sau lần đo đầu (xem file đó).</remarks>
public sealed class CommandLatencyBenchmark
{
    private const int Warmup = 50;
    private const int Measured = 1_000;

    /// <summary>Chạy riêng sau lượt test chung (Makefile, CI): chung lượt với các bộ test tích hợp khác thì p95 đo cả tranh CPU.</summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task One_thousand_sequential_commands()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });
        for (var i = 0; i < Warmup; i++)
        {
            h.Run(new CreateLabOrder { NId = $"WARM-{i}", MaterialNId = "MAT-1", Quantity = 1 }).StatusCode.ShouldBe(200);
        }

        var samples = new double[Measured];
        var total = Stopwatch.StartNew();
        for (var i = 0; i < Measured; i++)
        {
            var started = Stopwatch.GetTimestamp();
            var result = h.Run(new CreateLabOrder { NId = $"ORD-{i}", MaterialNId = "MAT-1", Quantity = 1 });
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            result.StatusCode.ShouldBe(200);
        }

        total.Stop();
        Array.Sort(samples);
        double P(double q) => samples[(int)Math.Ceiling(q * Measured) - 1];
        var line = string.Create(CultureInfo.InvariantCulture,
            $"commands={Measured} total={total.Elapsed.TotalSeconds:F1}s p50={P(0.50):F1}ms p95={P(0.95):F1}ms p99={P(0.99):F1}ms max={samples[^1]:F1}ms");
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        // Runner giữ output của test; file cạnh assembly để đọc số sau khi chạy (artifacts/ không commit).
        await File.AppendAllTextAsync(Path.Combine(AppContext.BaseDirectory, "command-latency.txt"),
            line + Environment.NewLine, TestContext.Current.CancellationToken);

        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrder>()}]").ShouldBe(Warmup + Measured);
        // Lần đo đầu 2026-10-27: p95 = 14,7 ms (benchmarks.md). Ngưỡng ~3 lần để chừa runner CI chậm hơn.
        P(0.95).ShouldBeLessThan(50);
    }
}
