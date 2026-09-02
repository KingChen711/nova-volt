using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.FactoryModel;
using Nvm.FactoryModel;
using Nvm.FactoryModel.Commands;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Audit;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.UnitTests.FactoryModel;

public sealed class ActivateFactoryModelRevisionTests
{
    private static readonly DateTimeOffset ShiftAStart = new(2026, 8, 25, 6, 0, 0, TimeSpan.FromHours(7));

    // Mỗi revision đã publish đã thay đổi gì trên plant, để các assertion bên dưới đọc lên đúng như
    // sự thay đổi đang được kiểm tra chứ không phải như những chuỗi ký tự vô nghĩa. r2 mở rộng
    // Formation cycler 1 từ bốn charging channel lên tám; r3 đưa cycler 2 đi overhaul dài hạn, thêm
    // một stacker thứ tư vào cell line 2, và — chỉ riêng ở Leipzig — thêm một end-of-line tester.
    private const string Cycler2 = "NOVAVOLT/NV1/FORMATION/F1/FORM-02";
    private const string Stacker4 = "NOVAVOLT/NV1/ASSEMBLY/L2/STACK-04";
    private const string LeipzigEolTester = "NOVAVOLT/DE1/PACK/P1/EOL-01";

    private static readonly string SeedDirectory = Path.Combine(AppContext.BaseDirectory, "seed");

    private static ServiceProvider BuildContainer(FakeTimeProvider clock) =>
        new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddNvmFactoryModel(SeedDirectory)
            .AddNvmKernel(typeof(ActivateFactoryModelRevisionCommand).Assembly)
            .BuildServiceProvider();

    /// <summary>Đưa một plant đi qua các revision đã cho và trả về event cuối cùng.</summary>
    private static async Task<FactoryModelRevisionActivated> RollForwardAsync(
        ICommandDispatcher dispatcher,
        string siteId,
        params int[] revisions)
    {
        FactoryModelRevisionActivated? last = null;

        foreach (var revision in revisions)
        {
            last = await dispatcher.DispatchAsync(
                Activate(siteId, revision),
                TestContext.Current.CancellationToken);
        }

        return last!;
    }

    private static ActivateFactoryModelRevisionCommand Activate(string siteId, int revision) =>
        new(ActivateFactoryModelRevisionCommand.KeyFor(siteId, revision), siteId, revision);

    [Fact]
    public async Task Activating_APlantForTheFirstTime_ReportsEveryNodeAsAdded()
    {
        // Chưa có gì đang in force cả, nên toàn bộ cây là mới. Event phải nói rõ điều đó thay vì báo
        // cáo một thay đổi rỗng: một consumer bắt đầu từ con số không cần bức tranh đầy đủ ngay từ
        // message đầu tiên.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var activated = await dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken);

        activated.SiteId.ShouldBe("NV1");
        activated.Revision.ShouldBe(1);
        activated.NodeCount.ShouldBe(activated.EquipmentPathsAdded.Count);
        activated.EquipmentPathsRemoved.ShouldBeEmpty();
        activated.EquipmentPathsAdded.ShouldContain("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");
    }

    [Fact]
    public async Task Activating_OnePlant_LeavesTheOtherAlone()
    {
        // Multiplant, được assert chứ không phải chỉ giả định. Một staged rollout nghĩa là Hai Phong
        // có thể chuyển trong khi Leipzig đứng yên, và một thay đổi ở một plant không bao giờ được
        // xuất hiện trong event của plant kia.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var active = container.GetRequiredService<IActiveFactoryModel>();

        var nv1 = await dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken);

        active.Current("DE1").ShouldBeNull();
        nv1.EquipmentPathsAdded.ShouldNotContain(path => path.StartsWith("NOVAVOLT/DE1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Activating_TheSameRevisionAgain_IsRefusedBecauseItMovesNothingForward()
    {
        // Không giống một duplicate. Một duplicate mang cùng idempotency key và được replay âm thầm;
        // đây là một ý định khác nhưng vô nghĩa, và việc phát ra một event thứ hai sẽ khiến mọi
        // consumer phải rebuild cache của nó cho một thay đổi chưa từng xảy ra.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        await dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken);

        var repeat = new ActivateFactoryModelRevisionCommand(
            IdempotencyKey.FromNaturalKey("factory-model", "second-attempt", "NV1"),
            "NV1",
            1);

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(repeat, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("would not move it forward");
    }

    [Fact]
    public async Task Activating_ARevisionTheCatalogDoesNotHold_IsRefusedAndNamesWhatExists()
    {
        // Revision là một guard, không phải một selector: caller nói rõ nó đã đọc revision nào. Từ
        // chối một con số chưa publish là cách giữ một work cell đã ngừng hoạt động khỏi shop floor —
        // và lời từ chối nêu tên cái shelf, vì "revision 99 does not exist" sẽ khiến operator phải
        // đoán xem họ gõ nhầm hay rollout chưa từng được publish.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(Activate("NV1", 99), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("revision 99");
        thrown.Message.ShouldContain("1, 2, 3");
    }

    [Fact]
    public async Task Activating_APlantThatIsNotInTheDocument_IsRefused()
    {
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(Activate("XX9", 1), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("XX9");
    }

    [Theory]
    [InlineData("nv1", 1, "lower-case site")]
    [InlineData("NV1", 0, "revision below 1")]
    [InlineData("", 1, "no site at all")]
    public async Task MalformedCommand_IsStoppedByValidationBeforeTheHandlerSeesIt(
        string siteId,
        int revision,
        string reason)
    {
        // Một vấn đề về hình dạng, nên nó fail như một validation error chứ không phải một business
        // refusal. Caller có thể phân biệt "sửa lại request của bạn" với "thế giới không ở trạng thái
        // bạn tưởng".
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        await Should.ThrowAsync<CommandValidationException>(
            () => dispatcher.DispatchAsync(
                new ActivateFactoryModelRevisionCommand(
                    IdempotencyKey.FromNaturalKey("factory-model", "malformed", siteId, reason),
                    siteId,
                    revision),
                TestContext.Current.CancellationToken),
            reason);
    }

    [Fact]
    public async Task SameActivationTwice_RunsOnceAndReplaysTheEvent()
    {
        // Vòng lặp đầy đủ mà milestone này thực sự nói tới: command, ba behaviour, handler, event —
        // và một lần gửi lại không thay đổi gì. AGENTS.md K7 được kiểm chứng qua một handler thật
        // thay vì một probe.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var sink = (InMemoryCommandAuditSink)container.GetRequiredService<ICommandAuditSink>();

        var first = await dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken);
        var second = await dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken);

        second.ShouldBe(first);
        sink.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EventId_EqualsTheCommandsIdempotencyKey()
    {
        // Điểm nối giữa hai lớp deduplication. Ingestion loại bỏ các device message lặp lại dựa trên
        // giá trị này và pipeline loại bỏ các command lặp lại cũng dựa trên nó; nếu event mang một id
        // khác, mỗi lớp sẽ được keyed theo một thứ mà lớp kia chưa từng thấy.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var command = Activate("NV1", 1);

        var activated = await dispatcher.DispatchAsync(command, TestContext.Current.CancellationToken);

        activated.EventId.ShouldBe(command.IdempotencyKey.Value);
    }

    [Fact]
    public async Task OccurredAt_ComesFromTheInjectedClock()
    {
        var clock = new FakeTimeProvider(ShiftAStart);
        await using var container = BuildContainer(clock);
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        clock.Advance(TimeSpan.FromHours(9));

        var activated = await dispatcher.DispatchAsync(Activate("DE1", 1), TestContext.Current.CancellationToken);

        activated.OccurredAt.ShouldBe(ShiftAStart.AddHours(9));
    }

    [Fact]
    public void Event_IsVersionedFromV1AndCarriesNoBareDateTime()
    {
        // AGENTS.md K6 và K2, được kiểm tra ngay trên event thật đầu tiên thay vì để dành cho
        // analyser xuất hiện sau này trong milestone. Một event không có version thì không thể
        // upcast vào năm 2036, và một DateTime không có offset thì mập mờ trong một giờ mỗi mùa thu
        // ở DE1.
        var eventType = typeof(FactoryModelRevisionActivated);

        eventType.GetCustomAttribute<EventVersionAttribute>()!.Version.ShouldBe(1);
        eventType.GetProperties().ShouldNotContain(property => property.PropertyType == typeof(DateTime));
    }

    [Fact]
    public void KeyFor_IsDeterministicAndSeparatesSitesAndRevisions()
    {
        ActivateFactoryModelRevisionCommand.KeyFor("NV1", 1)
            .ShouldBe(ActivateFactoryModelRevisionCommand.KeyFor("NV1", 1));

        ActivateFactoryModelRevisionCommand.KeyFor("NV1", 1)
            .ShouldNotBe(ActivateFactoryModelRevisionCommand.KeyFor("DE1", 1));

        ActivateFactoryModelRevisionCommand.KeyFor("NV1", 1)
            .ShouldNotBe(ActivateFactoryModelRevisionCommand.KeyFor("NV1", 2));
    }

    // ── Chuyển một plant từ revision này sang revision kế tiếp ──────────────────────────────────
    // Mọi thứ ở trên đều activate revision 1 trên một plant đang không chạy gì cả, nơi mọi path đều
    // mới và không gì bị gỡ bỏ. Đó là nửa dễ, và cho tới giờ đó là nửa duy nhất.

    [Fact]
    public async Task Activating_RevisionTwoWhileOnOne_ReportsTheNewChannelsAndRemovesNothing()
    {
        // Formation cycler 1 đi từ bốn charging channel lên tám. Mở rộng năng lực không lấy đi gì cả,
        // nên danh sách removed phải giữ rỗng: một diff báo cáo có biến động trên một phép cộng thuần
        // túy sẽ khiến mọi consumer phải rebuild cache cho thiết bị chưa từng dịch chuyển.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var activated = await RollForwardAsync(dispatcher, "NV1", 1, 2);

        activated.Revision.ShouldBe(2);
        activated.NodeCount.ShouldBe(35);
        activated.EquipmentPathsRemoved.ShouldBeEmpty();
        activated.EquipmentPathsAdded.ShouldBe([
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0005",
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0006",
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0007",
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0008"]);
    }

    [Fact]
    public async Task Activating_RevisionThreeWhileOnTwo_ReportsBothWhatArrivedAndWhatLeft()
    {
        // Trường hợp mà plan đã yêu cầu ở C08 nhưng code chưa làm được. Cycler 2 đi overhaul dài hạn
        // và một stacker thứ tư được lắp vào, nên đúng một path rời đi và một path xuất hiện — và
        // path rời đi chính là nửa của hợp đồng chưa từng chạy lần nào.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var active = container.GetRequiredService<IActiveFactoryModel>();

        await RollForwardAsync(dispatcher, "NV1", 1, 2);
        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(2);

        var activated = await dispatcher.DispatchAsync(
            Activate("NV1", 3),
            TestContext.Current.CancellationToken);

        activated.Revision.ShouldBe(3);
        activated.NodeCount.ShouldBe(35);
        activated.EquipmentPathsAdded.ShouldBe([Stacker4]);
        activated.EquipmentPathsRemoved.ShouldBe([Cycler2]);
        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(3);
    }

    [Fact]
    public async Task Activating_ARevisionBehindTheOneInForce_IsRefused()
    {
        // Rollback không phải một activation. Một plant đã báo cho cả thế giới biết rằng nó đã chuyển
        // sang 3 thì không thể lặng lẽ quay lại 2: stream khi đó sẽ mang hai tuyên bố mâu thuẫn nhau
        // và không consumer nào có thể xác định được cây nào đang thực sự ở trên sàn.
        //
        // Cố tình dùng một idempotency key mới. Dùng lại key từ bước trước sẽ bị replay như một
        // duplicate và trả về event cũ, đó là hành vi đúng nhưng sẽ chẳng kiểm chứng được gì về lời
        // từ chối.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        await RollForwardAsync(dispatcher, "NV1", 1, 2, 3);

        var rollback = new ActivateFactoryModelRevisionCommand(
            IdempotencyKey.FromNaturalKey("factory-model", "rollback-attempt", "NV1"),
            "NV1",
            2);

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(rollback, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("would not move it forward");
    }

    [Fact]
    public async Task TwoPlants_SitOnDifferentRevisions_WhichIsWhatAStagedRolloutIs()
    {
        // Hai Phong ở 3 trong khi Leipzig vẫn ở 1. Không phải drift cần được sửa — một rollout đến
        // từng plant một, và một model không thể diễn đạt điều này sẽ ép cả hai plant phải chuyển
        // cùng lúc, đó chính là điều một nhà máy không bao giờ làm.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
        var active = container.GetRequiredService<IActiveFactoryModel>();

        await RollForwardAsync(dispatcher, "NV1", 1, 2, 3);
        await dispatcher.DispatchAsync(Activate("DE1", 1), TestContext.Current.CancellationToken);

        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(3);
        active.Current("DE1").ShouldNotBeNull().Revision.ShouldBe(1);
    }

    [Fact]
    public async Task Activating_ASkippedRevision_DiffsAgainstWhatIsInForce_NotTheDocumentBeforeIt()
    {
        // Leipzig chưa từng nhận revision 2 — không có gì trong đó liên quan tới Leipzig. Đi thẳng từ
        // 1 lên 3 phải diff dựa trên những gì plant thực sự đang chạy, không phải dựa trên bất kỳ
        // document nào tình cờ nằm cạnh 3 trên shelf. Sai chỗ này sẽ báo cáo các channel của Hai
        // Phong như thể chúng vừa xuất hiện ở Leipzig.
        await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var activated = await RollForwardAsync(dispatcher, "DE1", 1, 3);

        activated.NodeCount.ShouldBe(10);
        activated.EquipmentPathsAdded.ShouldBe([LeipzigEolTester]);
        activated.EquipmentPathsRemoved.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheSameRollout_RunTwice_ProducesTheSameEventBothTimes()
    {
        // Deterministic, vì các event này cuối cùng sẽ nằm trong một audit trail và trong một golden
        // file. Một payload có thứ tự đến từ việc lặp qua hash sẽ khác nhau giữa hai lần chạy của
        // cùng một thay đổi, và sẽ chẳng còn gì có thể so sánh được với nhau nữa.
        var first = await RunRolloutAsync();
        var second = await RunRolloutAsync();

        second.EventId.ShouldBe(first.EventId);
        second.NodeCount.ShouldBe(first.NodeCount);
        second.EquipmentPathsAdded.ShouldBe(first.EquipmentPathsAdded);
        second.EquipmentPathsRemoved.ShouldBe(first.EquipmentPathsRemoved);

        async Task<FactoryModelRevisionActivated> RunRolloutAsync()
        {
            await using var container = BuildContainer(new FakeTimeProvider(ShiftAStart));
            using var scope = container.CreateScope();

            return await RollForwardAsync(
                scope.ServiceProvider.GetRequiredService<ICommandDispatcher>(),
                "NV1",
                1,
                2,
                3);
        }
    }

    [Fact]
    public async Task ActiveRevision_IsLostOnRestart_WhileTheCatalogIsNot()
    {
        // Ranh giới mà M1 dừng lại, được assert thay vì chỉ mô tả trong một comment không ai kiểm tra
        // lại. Các document là file, nên một lần restart sẽ đọc lại được cả ba; nhưng plant nào đang
        // ở revision nào thì sống trong RAM và đã mất. Một plant khởi động lại và tin rằng nó không
        // chạy gì cả sẽ báo cáo toàn bộ cây của nó như added ở lần activation kế tiếp. Giải quyết
        // việc này cần một database (M5).
        await using (var beforeRestart = BuildContainer(new FakeTimeProvider(ShiftAStart)))
        {
            using var scope = beforeRestart.CreateScope();
            await RollForwardAsync(
                scope.ServiceProvider.GetRequiredService<ICommandDispatcher>(),
                "NV1",
                1,
                2,
                3);

            beforeRestart.GetRequiredService<IActiveFactoryModel>()
                .Current("NV1").ShouldNotBeNull().Revision.ShouldBe(3);
        }

        await using var afterRestart = BuildContainer(new FakeTimeProvider(ShiftAStart));

        afterRestart.GetRequiredService<IActiveFactoryModel>().Current("NV1").ShouldBeNull();
        afterRestart.GetRequiredService<IFactoryModelCatalog>().Revisions.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Activating_WhenThePlantMovedUnderneath_IsRefusedRatherThanOverwriting()
    {
        // Compare-and-swap thua cuộc, nhìn từ phía handler thay vì từ phía store. Một scheduled
        // rollout và một engineer ở console cùng đọc một revision hiện tại như nhau; ai ghi sau thì
        // phải được báo là đã thua, vì "the plant is on revision 3" là một fact duy nhất và một work
        // cell hoặc đang trên sàn hoặc không.
        //
        // Được ép xảy ra bằng một stand-in thay vì một race thật, để nhánh này chạy trên mọi build
        // thay vì chỉ trên những build mà scheduler tình cờ interleave đúng cách.
        await using var container = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(ShiftAStart))
            .AddSingleton<IActiveFactoryModel>(new AlwaysLosesTheRace())
            .AddNvmFactoryModel(SeedDirectory)
            .AddNvmKernel(typeof(ActivateFactoryModelRevisionCommand).Assembly)
            .BuildServiceProvider();
        using var scope = container.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();

        var thrown = await Should.ThrowAsync<FactoryModelActivationException>(
            () => dispatcher.DispatchAsync(Activate("NV1", 1), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain("moved to another revision");
    }

    /// <summary>Một plant mà lúc nào cũng có người khác dịch chuyển trước.</summary>
    private sealed class AlwaysLosesTheRace : IActiveFactoryModel
    {
        public ActiveFactoryModelRevision? Current(string siteId) => null;

        public bool TryActivate(ActiveFactoryModelRevision revision, int? expectedCurrentRevision) => false;
    }
}
