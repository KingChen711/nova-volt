using System.Reflection;
using Nvm.Contracts.Events;

namespace Nvm.ArchitectureTests;

/// <summary>A4, A5 — hình dạng mà mọi event phải có, kiểm tra dựa trên metadata đã build.</summary>
/// <remarks>
/// <para>
/// A4 cố tình lặp lại điều mà analyzer <c>NVM002</c> đã từ chối, và sự lặp lại đó chính là điểm mấu
/// chốt. Một analyzer chạy bên trong compiler và có thể bị im lặng hóa từ ngay trong file nó đang
/// kiểm tra — một dòng <c>#pragma warning disable NVM002</c> là nó biến mất. Các test này đọc
/// <b>metadata của assembly đã được sinh ra</b>, nơi một pragma không để lại dấu vết nào: property
/// hoặc được khai kiểu <c>DateTime</c>, hoặc không.
/// </para>
/// <para>
/// Các control type dưới đây chứng minh điều đó thay vì chỉ khẳng định. Project này đặt
/// <c>UseNvmAnalyzers=false</c>, nên <c>EventWithForbiddenClock</c> đúng là file mà một analyzer chưa
/// bao giờ thấy — và cùng một predicate xóa sạch mọi event thật lại đánh dấu nó.
/// </para>
/// </remarks>
public sealed class EventContractTests
{
    private static IReadOnlyList<Type> ProductionEvents =>
        [.. NvmAssemblies.Contracts.GetTypes().Where(IsConcreteEvent)];

    [Fact]
    public void ThereAreEventsToCheckAtAll()
    {
        // Cái chốt bảo vệ khiến mọi test khác trong class này có ý nghĩa. Một filter viết sai tên trả
        // về một sequence rỗng, và mọi assertion "tất cả đều ổn" bên dưới đều pass.
        ProductionEvents.ShouldNotBeEmpty();
    }

    [Fact]
    public void A4_NoEventCarriesADateTimeAnywhereInItsShape()
    {
        // Site DE1 quan sát daylight saving, nên một giờ mỗi mùa thu xảy ra hai lần. DateTime ghi lại
        // giá trị wall-clock mà không có offset, event store là append-only, và sự mập mờ đó không
        // bao giờ có thể được sửa lại sau này (AGENTS.md K2).
        var offenders = ProductionEvents
            .SelectMany(MembersMentioningDateTime)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty($"DateTime reaches the wire through: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void A4_Control_TheDateTimeWalkFindsOneBuriedInAGeneric()
    {
        // Trường hợp lồng nhau chính là nơi loại kiểm tra này thường sai: chỉ so sánh type ngoài cùng
        // sẽ bỏ qua IReadOnlyList<DateTime>, thứ serialize hỏng y hệt như một field trần.
        MembersMentioningDateTime(typeof(EventWithForbiddenClock))
            .ShouldContain($"{nameof(EventWithForbiddenClock)}.{nameof(EventWithForbiddenClock.Samples)}");
    }

    [Fact]
    public void A5_EveryEventCarriesSiteId()
    {
        // Về mặt cấu trúc, đúng ngay từ hôm nay, vì IDomainEvent khai báo SiteId và compiler bắt buộc
        // interface. Vẫn viết ra đây: ngày nào đó có người publish một payload không implement cái
        // marker, đây là assertion đã sẵn sàng để chuyển hướng.
        foreach (var type in ProductionEvents)
        {
            type.GetProperty(nameof(IDomainEvent.SiteId))
                .ShouldNotBeNull($"{type.Name} has no SiteId (AGENTS.md K3)");
        }
    }

    [Fact]
    public void A5_SiteIdIsNeverNullable()
    {
        // Nửa còn lại mà interface không bắt buộc. Một SiteId nullable vẫn compile, vẫn thỏa
        // IDomainEvent, và tạo ra một routing key với đoạn đầu tiên rỗng — không khớp binding nào cả,
        // nên event không đến được với ai và không đâu báo lỗi cả.
        var context = new NullabilityInfoContext();

        foreach (var type in ProductionEvents)
        {
            var siteId = type.GetProperty(nameof(IDomainEvent.SiteId))!;

            context.Create(siteId).ReadState.ShouldBe(
                NullabilityState.NotNull,
                $"{type.Name}.SiteId is nullable (AGENTS.md K3)");
        }
    }

    [Fact]
    public void A5_EveryEventDeclaresItsWireNameAndVersion()
    {
        // Cả hai attribute, nếu không event sẽ vô hình với bus. DeclaredEventTypes.All() chọn dựa trên
        // IDomainEvent *và* [EventContract]; một event thiếu attribute sẽ không có exchange, không có
        // routing key và không có CloudEvents header, và publish vào topology mặc định của MassTransit
        // nơi không gì được bind. Nó chạy, nó không throw gì cả, và không ai nhận được nó.
        foreach (var type in ProductionEvents)
        {
            type.GetCustomAttribute<EventContractAttribute>()
                .ShouldNotBeNull($"{type.Name} has no [EventContract], so the bus cannot route it");

            type.GetCustomAttribute<EventVersionAttribute>()
                .ShouldNotBeNull($"{type.Name} has no [EventVersion] (AGENTS.md K6)")
                .Version.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void A5_Control_TheAttributeCheckSeesAnEventThatIsMissingThem()
    {
        // EventWithForbiddenClock không mang attribute nào và được compile với analyzer đã tắt.
        typeof(EventWithForbiddenClock).GetCustomAttribute<EventContractAttribute>().ShouldBeNull();
        typeof(EventWithForbiddenClock).GetCustomAttribute<EventVersionAttribute>().ShouldBeNull();
    }

    [Fact]
    public void EventsLiveOnlyInContracts()
    {
        // DeclaredEventTypes.All() chỉ quét đúng một assembly. Một event khai báo bên trong một
        // Functional Block không bao giờ được phát hiện, nên nó không bao giờ có topology — cùng một
        // sự im lặng như khi thiếu attribute, chỉ ở một tầng cao hơn.
        foreach (var assembly in new[] { NvmAssemblies.Kernel, NvmAssemblies.Bus, NvmAssemblies.FactoryModel })
        {
            assembly.GetTypes()
                .Where(IsConcreteEvent)
                .ShouldBeEmpty($"{assembly.GetName().Name} declares an event; events belong in Nvm.Contracts");
        }
    }

    private static bool IsConcreteEvent(Type type) =>
        type is { IsAbstract: false, IsInterface: false } && typeof(IDomainEvent).IsAssignableFrom(type);

    /// <summary>Nêu tên các member của một type mà declared type của nó nhắc tới <c>DateTime</c>.</summary>
    private static IEnumerable<string> MembersMentioningDateTime(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => Mentions(property.PropertyType))
            .Select(property => $"{type.Name}.{property.Name}");

    /// <summary><c>DateTime</c> có xuất hiện ở đâu đó bên trong một type hay không, dù lồng sâu đến đâu.</summary>
    /// <remarks>
    /// <c>DateTime?</c> là <c>Nullable&lt;DateTime&gt;</c> và <c>DateTime[]</c> là một array type, nên
    /// cả hai đều rơi ra từ đúng hai lần đệ quy đó thay vì cần case riêng của mình.
    /// </remarks>
    private static bool Mentions(Type type) =>
        type == typeof(DateTime)
        || (type.IsArray && Mentions(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(Mentions));

    /// <summary>Một event cố tình sai, để chứng minh các rule ở trên có khả năng fail.</summary>
    /// <remarks>
    /// Nó implement cái marker, giấu một <c>DateTime</c> bên trong một list, và không khai báo attribute
    /// contract nào cả — mọi lỗi mà A4 và A5 tìm kiếm, trong một type mà chưa analyzer nào từng kiểm tra.
    /// </remarks>
    public sealed record EventWithForbiddenClock(
        Guid EventId,
        DateTimeOffset OccurredAt,
        string SiteId,
        IReadOnlyList<DateTime> Samples) : IDomainEvent;
}
