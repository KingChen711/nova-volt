using Nvm.Foundation;
using Nvm.FoundationTests.LabModel;

namespace Nvm.FoundationTests;

/// <summary>Service layer, pre-check, dạng lỗi và trace (DoD R1 ý 3, 7, 8).</summary>
public sealed class PipelineBehaviorTests
{
    [Fact]
    public async Task Pre_check_blocks_only_in_the_app_that_enables_it()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        var viaLab = h.Run(new CreateLabOrder { NId = "ORD-1", MaterialNId = "MAT-1", Quantity = -5 }, LabApp.Name);
        var viaOther = h.Run(new CreateLabOrder { NId = "ORD-2", MaterialNId = "MAT-1", Quantity = -5 }, LabApp.OtherName);

        viaLab.StatusCode.ShouldBe(400);
        viaLab.Body["Error"]!["ErrorCode"]!.GetValue<int>().ShouldBe(1001);
        viaLab.Body["Error"]!["ErrorMessage"]!.GetValue<string>().ShouldStartWith("Pre-check 'RejectNegativeQuantity' rejected the command:");
        viaLab.Body["ExecutionDetail"]!["PreCheckDetails"]![0]!["PreCheck"]!.GetValue<string>().ShouldBe(nameof(RejectNegativeQuantity));
        // Cùng command, cùng dữ liệu, qua app không bật pre-check thì lọt.
        viaOther.StatusCode.ShouldBe(200);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrder>()}]").ShouldBe(1);
    }

    [Fact]
    public async Task Missing_mandatory_parameter_returns_6400_without_running_the_handler()
    {
        var h = await FoundationHarness.CreateAsync();

        var result = h.Run(new CreateLabOrder { NId = "ORD-1" });

        result.StatusCode.ShouldBe(400);
        result.Body["error"]!["code"]!.GetValue<string>().ShouldBe("6400");
        result.Body["error"]!["message"]!.GetValue<string>().ShouldContain("MaterialNId");
        // Dạng của service layer không có Succeeded; handler không chạy nên không có dòng "started".
        result.Body.ContainsKey("Succeeded").ShouldBeFalse();
        h.Traces.ByCorrelation(result.CorrelationId).ShouldNotContain(t => t.Message.Contains("started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handler_error_uses_the_error_and_succeeded_shape()
    {
        var h = await FoundationHarness.CreateAsync();

        var result = h.Run(new CreateLabOrder { NId = "ORD-1", MaterialNId = "NOPE" });

        result.StatusCode.ShouldBe(400);
        result.Body["Succeeded"]!.GetValue<bool>().ShouldBeFalse();
        result.Body["Error"]!["ErrorCode"]!.GetValue<int>().ShouldBe(2001);
        result.Body["Error"]!["ErrorMessage"]!.GetValue<string>().ShouldBe("Material NOPE does not exist.");
    }

    [Fact]
    public async Task Command_not_published_by_the_app_is_refused()
    {
        var h = await FoundationHarness.CreateAsync();

        var result = h.Run(new RegisterLabOrderWithSupplier { NId = "A", MaterialNId = "B", SupplierCode = "C" }, LabApp.OtherName);

        result.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task Trace_filtered_by_correlation_id_holds_the_whole_chain()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        var result = h.Run(new CreateLabOrderWithNote { NId = "ORD-1", MaterialNId = "MAT-1", FailNote = true });

        var chain = h.Traces.ByCorrelation(result.CorrelationId);
        var root = chain[0];
        // Lời gọi từ service layer: CorrelationId = CurrentId = RootRequestId, ParentId rỗng.
        root.CurrentId.ShouldBe(result.CorrelationId);
        root.RootRequestId.ShouldBe(result.CorrelationId);
        root.ParentId.ShouldBeNull();
        var children = chain.Where(t => t.ParentId == root.CurrentId).Select(t => t.ContextName).Distinct().ToArray();
        children.ShouldBe(["FB_OP_Lab.CreateLabOrder", "FB_OP_Lab.AddLabOrderNote"], ignoreOrder: true);
        chain.ShouldAllBe(t => t.RootRequestId == result.CorrelationId);
        // Câu chung ở response, lý do thật ở dòng Error của handler, và dòng rollback của platform.
        chain.ShouldContain(t => t.Level == TraceLevel.Error && t.Message == "Note failed: The note was rejected.");
        chain.ShouldContain(t => t.Message == "Transaction rolled back");
        h.Traces.Records.Count.ShouldBeGreaterThan(chain.Count);
    }

    [Fact]
    public async Task Trace_of_a_successful_call_contains_the_commit()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        var result = h.Run(new CreateLabOrder { NId = "ORD-1", MaterialNId = "MAT-1" });

        h.Traces.ByCorrelation(result.CorrelationId).ShouldContain(t => t.Message == "Commit completed");
    }
}
