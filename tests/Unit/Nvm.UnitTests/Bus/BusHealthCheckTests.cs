using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Nvm.Bus;
using Nvm.Hosting;

namespace Nvm.UnitTests.Bus;

public sealed class BusHealthCheckTests
{
    // Dùng async vì MassTransit đăng ký một singleton IAsyncDisposable, và dispose container theo
    // kiểu đồng bộ sẽ ném exception thay vì rơi về cách khác.
    private static async Task<HealthCheckRegistration> BusRegistrationAsync()
    {
        var services = new ServiceCollection();

        services.AddNvmBus(bus =>
        {
            bus.Username = "nvm";
            bus.Password = "irrelevant";
            bus.ApplicationName = "unit-tests";
        });

        // Không có gì ở đây kết nối tới broker: AddNvmBus chỉ xây dựng các registration, còn bus
        // được khởi động sau đó bởi một hosted service. Đó chính là tính chất mà outage lab dựa vào.
        await using var provider = services.BuildServiceProvider();

        return provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations
            .Single(registration => registration.Name == BusServiceCollectionExtensions.HealthCheckName);
    }

    [Fact]
    public async Task AddNvmBus_RegistersTheBusProbeUnderThisSystemsName()
    {
        // MassTransit gọi nó là "masstransit-bus" nếu không ai nói khác. Tên của một probe xuất hiện
        // trong dashboard, alert và runbook, nên nó được khai báo rõ ràng thay vì kế thừa từ bất kỳ
        // phiên bản library nào đang được pin.
        (await BusRegistrationAsync()).Name.ShouldBe("bus");
    }

    [Fact]
    public async Task AddNvmBus_TagsTheBusProbeReadyAndNothingElse()
    {
        // Vế quan trọng là nửa sau: KHÔNG phải live. Một bus không kết nối được broker là một bus
        // phải ngừng nhận traffic, không phải một process cần được restart — và một orchestrator
        // restart mọi instance trong lúc broker gặp sự cố chính là điều N15 cấm.
        (await BusRegistrationAsync()).Tags.ShouldBe([HealthTags.Ready]);
    }

    [Fact]
    public async Task AddNvmBus_FailsTheBusProbeAsUnhealthyRatherThanDegraded()
    {
        // Degraded trả về HTTP 200, điều này sẽ để instance tiếp tục nằm trong rotation trong khi bus
        // của nó không mang được message nào. Test này tồn tại vì sự khác biệt đó vô hình trong JSON
        // body và chỉ lộ ra dưới dạng một status code không ai đọc cho tới khi xảy ra sự cố.
        (await BusRegistrationAsync()).FailureStatus.ShouldBe(HealthStatus.Unhealthy);
    }
}
