using System.Text.Json;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.FoundationTests.LabModel;

namespace Nvm.FoundationTests;

/// <summary>App và extension app, state machine dạng dữ liệu, signal rule và timer, configuration key (R2).</summary>
public sealed class ConfigurationBehaviorTests
{
    private const string FoundationAppName = "Foundation";

    [Fact]
    public async Task Extension_app_command_runs_under_the_base_app_with_its_prefix()
    {
        var h = await FoundationHarness.CreateAsync();
        var tag = FoundationHarness.Registry.CommandOf(typeof(TagLabOrder));

        FoundationHarness.Registry.PublicNameIn(LabApp.Name, tag).ShouldBe("LabExt_TagLabOrder");
        FoundationHarness.Registry.PublicNameIn(LabApp.ExtensionName, tag).ShouldBeNull();
        h.Executor.Execute(LabApp.Name, "LabExt_TagLabOrder", Json("""{"OrderNId":"ORD-1"}""")).StatusCode.ShouldBe(200);
        h.Run(new TagLabOrder { OrderNId = "ORD-2" }, LabApp.ExtensionName).StatusCode.ShouldBe(404);
        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabOrderNote>()}]").ShouldBe(1);
    }

    [Fact]
    public async Task Block_command_imported_by_an_extension_app_is_published_on_the_base_app_with_its_prefix()
    {
        var h = await FoundationHarness.CreateAsync();
        var flag = FoundationHarness.Registry.CommandOf(typeof(FlagLabOrder));

        FoundationHarness.Registry.PublicNameIn(LabApp.Name, flag).ShouldBe("LabExt_FlagLabOrder");
        FoundationHarness.Registry.PublicNameIn(LabApp.ExtensionName, flag).ShouldBeNull();
        FoundationHarness.Registry.PublicNameIn(LabApp.OtherName, flag).ShouldBeNull();
        h.Executor.Execute(LabApp.Name, "LabExt_FlagLabOrder", Json("""{"OrderNId":"ORD-1"}""")).StatusCode.ShouldBe(200);
        h.Executor.Execute(LabApp.Name, "FlagLabOrder", Json("""{"OrderNId":"ORD-1"}""")).StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task Restricted_app_lets_a_command_through_only_for_its_roles_the_administrator_and_the_system()
    {
        var h = await FoundationHarness.CreateAsync(solution =>
        {
            solution.AdministratorRole = "admin";
            var lab = solution.App(LabApp.Name);
            lab.RestrictCommands = true;
            lab.Allow([nameof(CreateLabMaterial)], "planner");
        });
        var material = new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pc" };

        var refused = h.Run(material, caller: Caller("operator"));
        refused.StatusCode.ShouldBe(403);
        refused.Body["error"]!["code"]!.GetValue<string>().ShouldBe("6403");
        h.Run(new CreateLabMaterial { NId = "MAT-2", UnitOfMeasure = "pc" }, caller: Caller("planner")).StatusCode.ShouldBe(200);
        h.Run(new CreateLabMaterial { NId = "MAT-3", UnitOfMeasure = "pc" }, caller: Caller("admin")).StatusCode.ShouldBe(200);
        h.Run(new CreateLabOrder { NId = "ORD-1", MaterialNId = "MAT-2", Quantity = 1 }, caller: Caller("planner")).StatusCode.ShouldBe(403);
        h.Run(new CreateLabMaterial { NId = "MAT-4", UnitOfMeasure = "pc" }, caller: CallerIdentity.System).StatusCode.ShouldBe(200);
    }

    [Fact]
    public void Extension_app_cannot_extend_an_extension_app()
    {
        Should.Throw<InvalidOperationException>(() =>
                ModelRegistry.Build(FoundationHarness.LabTypes.Append(typeof(ModelErrors.ExtensionOfExtension))))
            .Message.ShouldContain("cannot extend extension app");
    }

    [Fact]
    public async Task Status_added_as_data_is_usable_without_code()
    {
        var h = await FoundationHarness.CreateAsync();
        await SeedOrderAsync(h);
        DefineMachine(h, "New", "Active");
        h.Run(new SetLabOrderStatus { OrderNId = "ORD-1", Status = "New" }).StatusCode.ShouldBe(200);

        // Trạng thái Frozen chưa có: không đi được.
        h.Run(new SetLabOrderStatus { OrderNId = "ORD-1", Status = "Frozen" }).Response!.Error!.ErrorCode.ShouldBe(7101);

        // Thêm status và transition chỉ bằng dữ liệu, không sửa code.
        Foundation(h, new DefineStatus { Machine = "LabOrder", Status = "Frozen" });
        Foundation(h, new DefineTransition { Machine = "LabOrder", FromStatus = "New", ToStatus = "Frozen" });
        h.Run(new SetLabOrderStatus { OrderNId = "ORD-1", Status = "Frozen" }).StatusCode.ShouldBe(200);
        h.Scalar<string>($"SELECT Status FROM dbo.[{h.TableOf<LabOrder>()}] WHERE NId = 'ORD-1'").ShouldBe("Frozen");
    }

    [Fact]
    public async Task Transition_with_roles_needs_one_of_them_and_one_without_roles_is_open()
    {
        var h = await FoundationHarness.CreateAsync();
        await SeedOrderAsync(h);
        DefineMachine(h, "New", "Active");
        Foundation(h, new AssignTransitionRole { Machine = "LabOrder", FromStatus = "New", ToStatus = "Active", Role = "shift-lead" });

        // Transition "" -> New không gán role: ai cũng đi được.
        h.Executor.Execute(LabApp.Name, new SetLabOrderStatus { OrderNId = "ORD-1", Status = "New" }, Caller("operator"))
            .StatusCode.ShouldBe(200);
        var refused = h.Executor.Execute(LabApp.Name, new SetLabOrderStatus { OrderNId = "ORD-1", Status = "Active" }, Caller("operator"));
        refused.Response!.Error!.ErrorCode.ShouldBe(7102);
        refused.Response.Error.ErrorMessage.ShouldContain("shift-lead");
        h.Executor.Execute(LabApp.Name, new SetLabOrderStatus { OrderNId = "ORD-1", Status = "Active" }, Caller("shift-lead"))
            .StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Transition_bound_to_a_role_nobody_has_blocks_everyone_lab_L06()
    {
        var h = await FoundationHarness.CreateAsync();
        await SeedOrderAsync(h);
        DefineMachine(h, "New", "Active");
        // Role gõ sai: lệnh gán role không kiểm role có tồn tại ở nhà cung cấp danh tính.
        Foundation(h, new AssignTransitionRole { Machine = "LabOrder", FromStatus = "New", ToStatus = "Active", Role = "shift_lead" });
        h.Run(new SetLabOrderStatus { OrderNId = "ORD-1", Status = "New" });

        foreach (var role in new[] { "shift-lead", "mom-admin", "operator" })
        {
            h.Executor.Execute(LabApp.Name, new SetLabOrderStatus { OrderNId = "ORD-1", Status = "Active" }, Caller(role))
                .Response!.Error!.ErrorCode.ShouldBe(7102);
        }
    }

    [Fact]
    public async Task Timer_rule_fires_once_per_interval_including_missed_ticks()
    {
        var h = await FoundationHarness.CreateAsync();
        Foundation(h, new CreateTimer { NId = "TICK-10M", FirstDueOn = h.Time.GetUtcNow().AddMinutes(10), IntervalSeconds = 600 });
        Foundation(h, new DefineSignalRule
        {
            NId = "RULE-TICK",
            Trigger = "Timer",
            TimerNId = "TICK-10M",
            App = LabApp.Name,
            Command = nameof(RecordLabTick),
            Parameters = """{"Source":"timer"}""",
        });

        h.Rules.RunDueTimers().ShouldBe(0);
        h.Time.Advance(TimeSpan.FromMinutes(60));
        h.Rules.RunDueTimers().ShouldBe(6);
        h.Rules.RunDueTimers().ShouldBe(0);
        h.Time.Advance(TimeSpan.FromMinutes(10));
        h.Rules.RunDueTimers().ShouldBe(1);

        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabTick>()}] WHERE Source = 'timer'").ShouldBe(7);
    }

    [Fact]
    public async Task One_shot_timer_fires_once_and_disables_itself()
    {
        var h = await FoundationHarness.CreateAsync();
        Foundation(h, new CreateTimer { NId = "ONCE", FirstDueOn = h.Time.GetUtcNow().AddMinutes(1) });
        Foundation(h, new DefineSignalRule
        {
            NId = "RULE-ONCE",
            Trigger = "Timer",
            TimerNId = "ONCE",
            App = LabApp.Name,
            Command = nameof(RecordLabTick),
        });

        h.Time.Advance(TimeSpan.FromHours(5));
        h.Rules.RunDueTimers().ShouldBe(1);
        h.Rules.RunDueTimers().ShouldBe(0);
        h.Scalar<bool>($"SELECT Enabled FROM dbo.[{h.TableOf<SystemTimer>()}] WHERE NId = 'ONCE'").ShouldBeFalse();
    }

    [Fact]
    public async Task Event_rule_maps_event_properties_into_the_command()
    {
        var h = await FoundationHarness.CreateAsync();
        Foundation(h, new DefineSignalRule
        {
            NId = "RULE-ON-ORDER",
            Trigger = "Event",
            EventType = "FB_OP_Lab.LabOrderCreated",
            App = LabApp.Name,
            Command = nameof(RecordLabTick),
            Parameters = """{"Source":"rule:{NId}"}""",
        });

        h.Rules.RunEventRules("FB_OP_Lab.LabOrderCreated", """{"OrderId":"00000000-0000-0000-0000-000000000001","NId":"ORD-9"}""",
            Guid.NewGuid()).ShouldBe(1);

        h.Scalar<int>($"SELECT COUNT(*) FROM dbo.[{h.TableOf<LabTick>()}] WHERE Source = 'rule:ORD-9'").ShouldBe(1);
    }

    [Fact]
    public async Task Configuration_key_is_stored_as_text_with_type_and_category()
    {
        var h = await FoundationHarness.CreateAsync();

        Foundation(h, new SetConfigurationKey { NId = "Lab.MaxTotes", Val = "16", Type = "Int32", Category = "Lab" });
        Foundation(h, new SetConfigurationKey { NId = "Lab.MaxTotes", Val = "18", Type = "Int32", Category = "Lab" });

        using var read = h.Executor.OpenRead();
        var key = read.SetOf<ConfigurationKey>().Single(k => k.NId == "Lab.MaxTotes");
        (key.Val, key.Type, key.Category).ShouldBe(("18", "Int32", "Lab"));
    }

    private static CallerIdentity Caller(params string[] roles) => new("tester", new HashSet<string>(roles, StringComparer.Ordinal));

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static void Foundation(FoundationHarness h, object command) => h.Run(command, FoundationAppName).StatusCode.ShouldBe(200);

    private static void DefineMachine(FoundationHarness h, params string[] statuses)
    {
        foreach (var status in statuses)
        {
            Foundation(h, new DefineStatus { Machine = "LabOrder", Status = status });
        }

        Foundation(h, new DefineTransition { Machine = "LabOrder", ToStatus = statuses[0] });
        for (var i = 1; i < statuses.Length; i++)
        {
            Foundation(h, new DefineTransition { Machine = "LabOrder", FromStatus = statuses[i - 1], ToStatus = statuses[i] });
        }
    }

    private static Task SeedOrderAsync(FoundationHarness h)
    {
        h.Run(new CreateLabMaterial { NId = "MAT-1", UnitOfMeasure = "pcs" }).StatusCode.ShouldBe(200);
        h.Run(new CreateLabOrder { NId = "ORD-1", MaterialNId = "MAT-1" }).StatusCode.ShouldBe(200);
        return Task.CompletedTask;
    }
}
