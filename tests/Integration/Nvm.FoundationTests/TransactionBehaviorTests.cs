using Nvm.Foundation.Runtime;
using Nvm.FoundationTests.LabModel;

namespace Nvm.FoundationTests;

/// <summary>Transaction: một khối trong cùng domain; composite và command khác domain thì không (DoD R1 ý 1, 2, 4, 5).</summary>
public sealed class TransactionBehaviorTests
{
    [Fact]
    public async Task Composite_keeps_the_first_call_when_the_second_call_fails()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" }).StatusCode.ShouldBe(200);

        var result = h.Run(new RegisterLabOrderWithSupplier { NId = "ORD-1", MaterialNId = "MAT-1", SupplierCode = "BAD" });

        result.StatusCode.ShouldBe(400);
        result.Response!.Error!.ErrorCode.ShouldBe(4001);
        // Lời gọi 1 đã commit trong transaction riêng: order còn, nhưng thiếu nhà cung cấp (lab L01).
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrder>()}] WHERE NId = 'ORD-1' AND IsDeleted = 0").ShouldBe(1);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrderSupplier>()}] WHERE FacetType = 'LabOrderSupplier'").ShouldBe(0);
    }

    [Fact]
    public async Task Call_command_chain_in_a_block_rolls_back_the_parent_when_the_child_fails()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        var result = h.Run(new CreateLabOrderWithNote { NId = "ORD-2", MaterialNId = "MAT-1", FailNote = true });

        result.StatusCode.ShouldBe(400);
        // CreateLabOrder (con thứ nhất) đã Submit trong cùng transaction; con thứ hai lỗi, cha trả lỗi, nên mất hết.
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrder>()}]").ShouldBe(0);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrderNote>()}]").ShouldBe(0);
        h.Events.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Child_failure_ignored_by_the_parent_commits_by_default()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        var result = h.Run(new CreateLabOrderWithNote { NId = "ORD-3", MaterialNId = "MAT-1", FailNote = true, IgnoreChildFailure = true });

        // Chưa xác minh trên hệ thật (scope §7): mặc định response của command gốc quyết định.
        result.StatusCode.ShouldBe(200);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrder>()}]").ShouldBe(1);
    }

    [Fact]
    public async Task Child_failure_ignored_by_the_parent_rolls_back_when_configured_to_doom()
    {
        var h = await FoundationHarness.CreateAsync(s => s.ChildFailureDoomsTransaction = true);
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        var result = h.Run(new CreateLabOrderWithNote { NId = "ORD-4", MaterialNId = "MAT-1", FailNote = true, IgnoreChildFailure = true });

        result.StatusCode.ShouldBe(400);
        result.Response!.Error!.ErrorCode.ShouldBe(3001);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrder>()}]").ShouldBe(0);
    }

    [Fact]
    public async Task Call_command_to_another_domain_commits_on_its_own()
    {
        var h = await FoundationHarness.CreateAsync();

        var result = h.Run(new CreateLabOrderWithMaterial { MaterialNId = "MAT-X", FailAfterMaterial = true });

        result.StatusCode.ShouldBe(400);
        // Master data nằm ngoài transaction vận hành: lỗi của command cha không kéo lại.
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabMaterial>()}] WHERE NId = 'MAT-X'").ShouldBe(1);
    }

    [Fact]
    public async Task Post_action_failure_fails_the_command_and_writes_nothing()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        var result = h.Run(new CreateLabOrder { NId = "POISON-1", MaterialNId = "MAT-1" });

        result.StatusCode.ShouldBe(400);
        result.Response!.Error!.ErrorCode.ShouldBe(5001);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrder>()}]").ShouldBe(0);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrderCounters>()}]").ShouldBe(0);
        h.Events.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Post_action_runs_in_every_app()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        h.Run(new CreateLabOrder { NId = "POISON-2", MaterialNId = "MAT-1" }, LabApp.OtherName).StatusCode.ShouldBe(400);
        h.Run(new CreateLabOrder { NId = "ORD-5", MaterialNId = "MAT-1" }, LabApp.OtherName).StatusCode.ShouldBe(200);

        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrderCounters>()}] WHERE LabOrderCounters_CreatedCount = 1").ShouldBe(1);
    }

    [Fact]
    public async Task Optional_post_action_can_be_disabled_in_the_solution()
    {
        var h = await FoundationHarness.CreateAsync(s => s.DisabledPostActions.Add(nameof(CountOrderCreation)));
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        h.Run(new CreateLabOrder { NId = "ORD-6", MaterialNId = "MAT-1" }).StatusCode.ShouldBe(200);

        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrderCounters>()}]").ShouldBe(0);
    }

    [Fact]
    public async Task Commit_failure_publishes_no_event_and_keeps_nothing()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });
        // Bảng mà bước commit cần không còn (như sự cố thiếu bảng): handler chạy xong, commit thì hỏng.
        h.Execute($"EXEC sp_rename 'dbo.{SchemaUpdater.EventTable}', 'EventLogMoved';");

        var result = h.Run(new CreateLabOrder { NId = "ORD-7", MaterialNId = "MAT-1" });

        result.StatusCode.ShouldBe(400);
        h.Events.Published.ShouldBeEmpty();
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrder>()}]").ShouldBe(0);
        h.Traces.ByCorrelation(result.CorrelationId).ShouldContain(t => t.Message.StartsWith("Commit failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commit_failure_can_still_report_succeeded_for_lab_L02()
    {
        var h = await FoundationHarness.CreateAsync(s => s.CommitFailureReporting = CommitFailureReporting.HandlerResult);
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });
        h.Execute($"EXEC sp_rename 'dbo.{SchemaUpdater.EventTable}', 'EventLogMoved';");

        var result = h.Run(new CreateLabOrder { NId = "ORD-8", MaterialNId = "MAT-1" });

        result.StatusCode.ShouldBe(200);
        result.As<CreatedResponse>().Succeeded.ShouldBeTrue();
        // Response nói thành công nhưng đọc lại theo Id không thấy: lý do chỉ có trong trace.
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrder>()}] WHERE Id = '{result.As<CreatedResponse>().Id}'").ShouldBe(0);
    }

    [Fact]
    public async Task Event_is_stored_and_published_after_commit()
    {
        var h = await FoundationHarness.CreateAsync();
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" });

        var result = h.Run(new CreateLabOrder { NId = "ORD-9", MaterialNId = "MAT-1" });

        var published = h.Events.Published.ShouldHaveSingleItem();
        published.EventType.ShouldBe("FB_OP_Lab.LabOrderCreated");
        published.Version.ShouldBe(1);
        published.CorrelationId.ShouldBe(result.CorrelationId);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.{SchemaUpdater.EventTable} WHERE EventId = '{published.EventId}'").ShouldBe(1);
    }
}
