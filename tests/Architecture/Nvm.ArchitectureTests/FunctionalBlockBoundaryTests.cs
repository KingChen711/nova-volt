using System.Reflection;

namespace Nvm.ArchitectureTests;

/// <summary>A3, A6 — những gì một Functional Block được phép vươn tới.</summary>
/// <remarks>
/// <para>
/// Một Functional Block là một bounded context với entity, command và schema của riêng nó. Nó nói
/// chuyện với các block khác qua <b>contract và bus</b>, không bao giờ bằng cách gọi trực tiếp chúng.
/// Ngay khoảnh khắc một block reference một block khác, cả hai chia sẻ chung một deployment, một
/// nhịp độ release và một điểm hỏng — và một từ mang một nghĩa ở block này giờ phải mang cùng một
/// nghĩa ở cả hai (AGENTS.md K8).
/// </para>
/// <para>
/// Nêu ra dưới dạng một <b>allowlist</b>, không phải một danh sách block bị cấm. Một denylist phải
/// được sửa mỗi khi có block mới thêm vào, bởi chính người thêm nó, trong một file họ không có lý do
/// gì để mở ra — nên block đầu tiên bị bỏ quên chính là block mà rule này sinh ra để dành cho. Một
/// allowlist từ chối bất kỳ thứ gì mới theo mặc định.
/// </para>
/// </remarks>
public sealed class FunctionalBlockBoundaryTests
{
    /// <summary>Những assembly <c>Nvm.*</c> duy nhất mà một Functional Block được phép reference.</summary>
    /// <remarks>
    /// Ba tầng Platform, và danh sách chỉ lớn thêm khi một tầng ngang thực sự thuộc về mọi block.
    /// <c>Nvm.Time</c> được thêm ở M3: time zone của một plant sống trong factory model, nên block sở
    /// hữu cây plant chính là block trả lời <c>ISiteCalendarDirectory</c>. Dependency chạy theo chiều
    /// block → Platform, đúng là chiều đã được cho phép từ trước.
    /// </remarks>
    private static readonly string[] AllowedForFunctionalBlocks =
        ["Nvm.Contracts", "Nvm.Kernel", "Nvm.Time"];

    [Fact]
    public void A3_FactoryModel_ReferencesOnlyContractsAndKernel()
    {
        var forbidden = NvmAssemblies.NvmReferencesOf(NvmAssemblies.FactoryModel)
            .Where(name => !AllowedForFunctionalBlocks.Contains(name, StringComparer.Ordinal))
            .ToList();

        forbidden.ShouldBeEmpty(
            $"A Functional Block may reference {string.Join(" and ", AllowedForFunctionalBlocks)} only; "
            + $"Nvm.FactoryModel also references {string.Join(", ", forbidden)}");
    }

    [Fact]
    public void A3_Control_TheAllowlistCanActuallyRejectSomething()
    {
        // Áp dụng lên chính test assembly này, thứ reference Nvm.Bus và Nvm.FactoryModel và chắc chắn
        // không phải một Functional Block. Nếu cái này trả về rỗng, A3 sẽ chỉ là một assertion rằng
        // một danh sách rỗng thì rỗng.
        NvmAssemblies.NvmReferencesOf(Assembly.GetExecutingAssembly())
            .Where(name => !AllowedForFunctionalBlocks.Contains(name, StringComparer.Ordinal))
            .ShouldNotBeEmpty();
    }

    [Fact]
    public void A3_Control_TheReferenceReaderSeesSomethingAtAll()
    {
        // Bảo vệ trường hợp NvmReferencesOf trả về rỗng vì prefix viết sai hoặc compiler đã cắt bỏ một
        // reference không dùng tới. FactoryModel thực sự dùng cả hai.
        NvmAssemblies.NvmReferencesOf(NvmAssemblies.FactoryModel)
            .ShouldBe(AllowedForFunctionalBlocks);
    }

    [Fact]
    public void A6_FactoryModel_DoesNotChooseItsOwnTransport()
    {
        // Một trường hợp đặc biệt của A3, giữ tách riêng vì lý do khác nhau. Một Functional Block tạo
        // ra một event; quyết định rằng event đó đi qua RabbitMQ là việc của host. Một block reference
        // Nvm.Bus thì không thể unit test được nếu thiếu broker, và không thể tái sử dụng bởi một host
        // publish theo cách khác.
        NvmAssemblies.NvmReferencesOf(NvmAssemblies.FactoryModel)
            .ShouldNotContain("Nvm.Bus");
    }

    [Fact]
    public void A6_Control_TheCheckWouldSeeNvmBusIfItWereThere()
    {
        // Có thứ gì đó trong solution thực sự reference Nvm.Bus, và cái này chứng minh cái tên đang
        // được tìm kiếm được viết đúng như cách assembly thực sự được đặt tên.
        Assembly.GetExecutingAssembly()
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ShouldContain("Nvm.Bus");
    }
}
