using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;

namespace Nvm.UnitTests.FactoryModel;

/// <summary>
/// Activation là một read, một decision và một write, và có thể có chuyện xảy ra ở giữa. Các test
/// này ghim chặt compare-and-swap để biến ba bước đó thành một.
/// </summary>
public sealed class ActiveFactoryModelConcurrencyTests
{
    private static readonly string SeedPath =
        Path.Combine(AppContext.BaseDirectory, "seed", FactoryModelSeed.FileNameFor(1));

    private static FactorySite Site() =>
        FactoryModelSeed.Load(SeedPath).FindSite("NV1")
            ?? throw new InvalidOperationException("The seed file no longer describes plant NV1.");

    [Fact]
    public void ActivatingOnAStaleRead_IsRefusedRatherThanOverwriting()
    {
        // Kịch bản: một scheduled rollout và một engineer đều đọc "chưa có gì in force cả", và cả hai
        // đều quyết định revision của mình là cái cần cài đặt. Không có guard, plant sẽ dừng ở bất kỳ
        // ai ghi sau, và hai event sẽ được phát ra, mỗi cái đều tự nhận đã đưa plant tiến lên từ cùng
        // một điểm xuất phát.
        var site = Site();
        var active = new InMemoryActiveFactoryModel();

        active.TryActivate(new ActiveFactoryModelRevision(1, site), expectedCurrentRevision: null)
            .ShouldBeTrue();

        active.TryActivate(new ActiveFactoryModelRevision(2, site), expectedCurrentRevision: null)
            .ShouldBeFalse();

        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(1);
    }

    [Fact]
    public void ActivatingOnAFreshRead_Succeeds()
    {
        // Nửa còn lại. Một guard từ chối mọi thứ cũng sẽ pass test ở trên, nên bước dịch chuyển hợp lệ
        // phải được assert ngay bên cạnh nó.
        var site = Site();
        var active = new InMemoryActiveFactoryModel();

        active.TryActivate(new ActiveFactoryModelRevision(1, site), expectedCurrentRevision: null).ShouldBeTrue();
        active.TryActivate(new ActiveFactoryModelRevision(2, site), expectedCurrentRevision: 1).ShouldBeTrue();

        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(2);
    }

    [Fact]
    public async Task SixteenActivationsAtOnce_ExactlyOneWins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Mọi caller đều đọc cùng một revision hiện tại rồi cố dịch chuyển plant từ đó. Đúng một
        // caller phải thắng và mười lăm caller còn lại phải được báo là đã thua, vì "the plant is on
        // revision 12" là một fact duy nhất và một work cell hoặc tồn tại trên shop floor hoặc không.
        const int callers = 16;
        var site = Site();
        var active = new InMemoryActiveFactoryModel();
        active.TryActivate(new ActiveFactoryModelRevision(1, site), expectedCurrentRevision: null).ShouldBeTrue();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var arrived = new CountdownEvent(callers);

        var attempts = Enumerable.Range(2, callers)
            .Select(revision => Task.Run(
                async () =>
                {
                    arrived.Signal();
                    await start.Task;

                    return active.TryActivate(
                        new ActiveFactoryModelRevision(revision, site),
                        expectedCurrentRevision: 1);
                },
                cancellationToken))
            .ToArray();

        arrived.Wait(TimeSpan.FromSeconds(30), cancellationToken).ShouldBeTrue();
        start.SetResult();

        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(activated => activated).ShouldBe(1);
        active.Current("NV1").ShouldNotBeNull().Revision.ShouldNotBe(1);
    }

    [Fact]
    public void TwoPlantsMovingAtOnce_DoNotBlockOrOverwriteEachOther()
    {
        // Staged rollout. Hai Phong và Leipzig là hai fact riêng biệt, và một guard được viết như một
        // lock duy nhất trên một slot duy nhất sẽ biến chúng thành một.
        var snapshot = FactoryModelSeed.Load(SeedPath);
        var haiPhong = snapshot.FindSite("NV1").ShouldNotBeNull();
        var leipzig = snapshot.FindSite("DE1").ShouldNotBeNull();
        var active = new InMemoryActiveFactoryModel();

        active.TryActivate(new ActiveFactoryModelRevision(11, haiPhong), null).ShouldBeTrue();
        active.TryActivate(new ActiveFactoryModelRevision(11, leipzig), null).ShouldBeTrue();
        active.TryActivate(new ActiveFactoryModelRevision(12, haiPhong), 11).ShouldBeTrue();

        active.Current("NV1").ShouldNotBeNull().Revision.ShouldBe(12);
        active.Current("DE1").ShouldNotBeNull().Revision.ShouldBe(11);
    }
}
