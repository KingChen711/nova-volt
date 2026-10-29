using Nvm.Foundation;
using Nvm.Product;
using Nvm.Project;
using Nvm.Solution.Suites;

namespace Nvm.SolutionTests;

/// <summary>Lab sự cố của R4: tái hiện, triệu chứng, chẩn đoán, cách sửa, đối chứng.</summary>
public sealed class LabTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task L06_role_on_frozen_to_aborted_that_nobody_has_blocks_every_abort_until_an_existing_role_is_assigned()
    {
        var h = await SolutionHarness.WithMasterDataAsync();
        h.RunClockUntil(Monday.AddHours(19));
        var frozen = h.Read<WorkOrder>(q => q.Where(w => w.Status == NvWorkOrderStates.Frozen)).First().NId;

        // Triệu chứng: kể cả quản trị cũng bị pre-check chặn; thân lỗi nêu tên pre-check.
        foreach (var caller in new[] { SolutionHarness.Caller("admin.nv1", "mom-admin"), SolutionHarness.Caller("lead.nv1", "line-lead") })
        {
            var refused = h.Run(new AbortWorkOrder { NId = frozen }, caller);
            refused.StatusCode.ShouldBe(400);
            refused.Body.ToJsonString().ShouldContain("CheckUserHasCorrectRoleToAbort");
            refused.Body.ToJsonString().ShouldContain("User is not allowed to Abort Work Order.");
        }

        // Chẩn đoán: role của transition không phải role nào trong realm.
        h.Read<TransitionRole>(q => q.Where(r => r.TransitionNId == "WorkOrder:Frozen->Aborted")).Select(r => r.Role).ShouldBe(["planning-admin"]);

        // Sửa: gán thêm một role có thật cho transition, bằng dữ liệu.
        h.Run(new AssignTransitionRole { Machine = "WorkOrder", FromStatus = "Frozen", ToStatus = "Aborted", Role = "line-lead" }, app: "Foundation")
            .StatusCode.ShouldBe(200);
        h.Run(new AbortWorkOrder { NId = frozen }, SolutionHarness.Caller("lead.nv1", "line-lead")).StatusCode.ShouldBe(200);
        h.Read<WorkOrder>(q => q.Where(w => w.NId == frozen)).Single().Status.ShouldBe(WorkOrderStates.Aborted);
    }

    [Fact]
    public async Task L07_failed_suite_written_to_history_is_skipped_by_the_next_deploy_and_only_counting_data_shows_it()
    {
        var root = CopySuites();
        var materials = Path.Combine(root, "master-data", "Production", "materials.csv");
        var good = Lines(materials);
        WriteLines(materials, [good[0], "NV-BROKEN,Broken row,,false,Raw", .. good[1..]]);
        var h = await SolutionHarness.CreateAsync();

        // Tái hiện: deploy lỗi, nhưng biến thể "luôn ghi lịch sử" vẫn ghi tên suite.
        var broken = h.Deploy(root, HistoryPolicy.Always);
        broken.Single(s => s.Suite == "Production_002_Materials").Status.ShouldBe(SuiteStatus.Failed);
        Lines(h.HistoryFile).ShouldContain("Production_002_Materials");

        // Sửa CSV rồi deploy lại: suite bị bỏ qua, dữ liệu vẫn thiếu. Lịch sử không nói lên gì; đếm dữ liệu mới thấy.
        WriteLines(materials, good);
        h.Deploy(root, HistoryPolicy.Always).Single(s => s.Suite == "Production_002_Materials").Status.ShouldBe(SuiteStatus.Skipped);
        // Chỉ còn kit của suite thành phẩm (suite riêng, không lỗi).
        h.Read<Material>().Select(m => m.NId).ShouldBe(["NV-FG-KIT-A"]);

        // Sửa: bỏ khỏi lịch sử tên suite lỗi và các suite lỗi theo vì thiếu material, rồi chạy lại.
        string[] retry = ["_002_Materials", "_003_Processes", "_008_FinishedGoodsProcesses", "_009_MaterialProperties"];
        WriteLines(h.HistoryFile, Lines(h.HistoryFile).Where(l => !retry.Any(r => l.Contains(r, StringComparison.Ordinal))
            && !l.StartsWith("NvProductionExt_", StringComparison.Ordinal)));
        h.Deploy(root, HistoryPolicy.Always).Where(s => s.Status == SuiteStatus.Failed).ShouldBeEmpty();
        h.Read<Material>().Count.ShouldBe(15);
    }

    [Fact]
    public async Task L07_control_history_written_only_on_success_lets_the_next_deploy_retry()
    {
        var root = CopySuites();
        var materials = Path.Combine(root, "master-data", "Production", "materials.csv");
        var good = Lines(materials);
        WriteLines(materials, [good[0], "NV-BROKEN,Broken row,,false,Raw", .. good[1..]]);
        var h = await SolutionHarness.CreateAsync();

        h.Deploy(root).Single(s => s.Suite == "Production_002_Materials").Status.ShouldBe(SuiteStatus.Failed);
        Lines(h.HistoryFile).ShouldNotContain("Production_002_Materials");

        WriteLines(materials, good);
        h.Deploy(root).Single(s => s.Suite == "Production_002_Materials").Status.ShouldBe(SuiteStatus.Executed);
        h.Read<Material>().Count.ShouldBe(15);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(420, 19)]
    public async Task L08_shift_timer_uses_server_time_while_the_window_check_uses_utc(int offsetMinutes, int windowStartHour)
    {
        // Server UTC+7: timer "07:00" chạy lúc 00:00Z, phép so khung ca theo phút UTC rơi vào ca đêm (19:00-06:59),
        // nên timer đầu ca sáng lại mở WO của ca đêm. Server UTC: WO mở đúng ca của timer.
        // Bắt đầu một giờ trước timer ca sáng, để timer ca đêm chưa tới hạn.
        var h = await SolutionHarness.CreateAsync(Monday.AddHours(6).AddMinutes(-offsetMinutes));
        h.Run(new SetConfigurationKey { NId = NvKeys.ServerUtcOffsetMinutes, Val = offsetMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            app: "Foundation").StatusCode.ShouldBe(200);
        h.Deploy().Where(s => s.Status == SuiteStatus.Failed).ShouldBeEmpty();

        var morningTimer = h.Read<SystemTimer>(q => q.Where(t => t.NId == "DAILY_WO_PS_NV1-B1-L1-ELEC-S1")).Single().NextDueOn!.Value;
        morningTimer.ShouldBe(Monday.AddHours(7).AddMinutes(-offsetMinutes));
        h.RunClockUntil(morningTimer);

        var opened = h.Read<WorkOrder>(q => q.Where(w => w.ProcessNId == "Coat_LN1")).ShouldHaveSingleItem();
        opened.EstimatedStart!.Value.UtcDateTime.Hour.ShouldBe(windowStartHour);
    }

    [Fact]
    public async Task L16_suite_reading_a_missing_csv_column_stays_green_and_changes_only_the_first_material()
    {
        var h = await SolutionHarness.WithMasterDataAsync();

        var broken = SuiteRunner.Run(Path.Combine(SolutionHarness.Fixtures(), "l16", "zz_SetLabClass.suite.json"), h.Target());

        broken.Passed.ShouldBeTrue();
        broken.Log.Count(l => l.EndsWith("Passed", StringComparison.Ordinal)).ShouldBe(12);
        h.Read<Material>(q => q.Where(m => m.MaterialClass == "Lab16")).Count.ShouldBe(1);

        var fixedSuite = SuiteRunner.Run(Path.Combine(SolutionHarness.Fixtures(), "l16", "zz_SetLabClassFixed.suite.json"), h.Target());

        fixedSuite.Passed.ShouldBeTrue();
        h.Read<Material>(q => q.Where(m => m.MaterialClass == "Lab16" && m.NId.StartsWith("NV-WIP-"))).Count.ShouldBe(6);
    }

    private static string[] Lines(string path) => File.ReadAllLines(path);

    private static void WriteLines(string path, IEnumerable<string> lines) => File.WriteAllLines(path, lines);

    private static string CopySuites()
    {
        var target = Path.Combine(Path.GetTempPath(), "nvs-suites-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.GetFiles(SolutionHarness.SuitesRoot, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(SolutionHarness.SuitesRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        return target;
    }
}
