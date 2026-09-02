using MassTransit;

namespace Nvm.UnitTests.Bus;

public sealed class MassTransitPinTests
{
    [Fact]
    public void MassTransit_StaysOnTheMajorVersionThatIsStillOpenSource()
    {
        // ADR-021, được thực thi thay vì chỉ ghi trên giấy. MassTransit 9 chuyển sang giấy phép
        // thương mại (massient.com/license); phiên bản 8 vẫn là Apache-2.0. Một lần "update all
        // packages" thông thường sẽ âm thầm kéo v9 vào, và không có gì trong code trông khác đi cả —
        // sự khác biệt chỉ lộ ra khi audit giấy phép.
        //
        // Nếu test này đỏ, đừng tăng con số lên. Đọc ADR-021 trước, rồi quyết định một cách có chủ ý.
        var version = typeof(IBus).Assembly.GetName().Version;

        version.ShouldNotBeNull();
        version.Major.ShouldBe(8, "MassTransit 9 and later require a paid licence — see docs/adr/ADR-021");
    }

    [Fact]
    public void MassTransit_RunsOnTheTargetFrameworkThisRepositoryBuilds()
    {
        // Nửa còn lại của việc pin phiên bản. Ở lại v8 chỉ khả thi chừng nào v8 còn ship một build
        // net10.0; ngày nó không còn ship nữa, quyết định về giấy phép phải được mở lại chứ không
        // phải tìm cách lách qua. Chạm vào một type của MassTransit từ một assembly net10.0 chính là
        // cách chứng minh điều đó ngay hôm nay.
        typeof(IBus).Assembly.GetName().Name.ShouldBe("MassTransit.Abstractions");
    }
}
