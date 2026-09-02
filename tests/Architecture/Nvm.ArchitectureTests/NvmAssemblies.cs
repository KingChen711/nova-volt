using System.Reflection;
using Nvm.Bus;
using Nvm.Contracts.Events;
using Nvm.FactoryModel;
using Nvm.Kernel.Commands;
using Nvm.Sparkplug;
using Nvm.Time;

namespace Nvm.ArchitectureTests;

/// <summary>Các assembly mà những rule này nói đến, tiếp cận qua một type thay vì bằng tên.</summary>
/// <remarks>
/// <c>typeof(X).Assembly</c> thay vì <c>Assembly.Load("Nvm.Kernel")</c>: một lỗi đánh máy trong một
/// chuỗi tạo ra một rule ném exception hoặc, tệ hơn, âm thầm không kiểm tra gì cả. Một lỗi đánh máy
/// trong tên type thì không compile được.
/// </remarks>
internal static class NvmAssemblies
{
    internal static Assembly Contracts => typeof(IDomainEvent).Assembly;

    internal static Assembly Kernel => typeof(ICommandDispatcher).Assembly;

    internal static Assembly Bus => typeof(BusServiceCollectionExtensions).Assembly;

    internal static Assembly FactoryModel => typeof(FactoryModelServiceCollectionExtensions).Assembly;

    internal static Assembly Sparkplug => typeof(SparkplugPayload).Assembly;

    internal static Assembly Time => typeof(IProductionCalendar).Assembly;

    /// <summary>Những tên một assembly được phép reference mà vẫn tính là "chỉ BCL".</summary>
    internal static bool IsBcl(string name) =>
        name is "netstandard" or "mscorlib" or "System"
        || name.StartsWith("System.", StringComparison.Ordinal);

    /// <summary>Mọi tên assembly mà một assembly cho trước reference, theo thứ tự.</summary>
    /// <remarks>
    /// Cùng một lưu ý như <see cref="NvmReferencesOf"/>: đây là cái runtime cần, không phải cái csproj
    /// liệt kê. Một package chỉ dùng cho một <c>const</c> bị compile bỏ đi và không xuất hiện.
    /// </remarks>
    internal static IReadOnlyList<string> NamesReferencedBy(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Order(StringComparer.Ordinal)];

    /// <summary>Các assembly <c>Nvm.*</c> mà một assembly cho trước reference.</summary>
    /// <remarks>
    /// <para>
    /// Đây là danh sách mà runtime cần, <b>không phải</b> danh sách các mục <c>ProjectReference</c>.
    /// Một reference mà công dụng duy nhất là một <c>const</c> bị compile bỏ hoàn toàn: giá trị được
    /// inline và assembly đó không bao giờ xuất hiện trong metadata. Đo trên <c>Nvm.Bus</c>, thứ
    /// reference <c>Nvm.Hosting</c> trong csproj của nó chỉ vì <c>HealthTags.Ready</c> và ở đây chỉ
    /// liệt kê <c>Nvm.Contracts</c>.
    /// </para>
    /// <para>
    /// Dù sao đó cũng là danh sách đúng cho các rule này. Điều K8 và K9 cấm là một component có khả
    /// năng <i>gọi</i> một component khác, và một dependency mà compiler đã xóa thì không thể gọi
    /// được. Một rule đọc csproj thay vào đó sẽ làm fail một block chỉ vì mượn một hằng số.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> NvmReferencesOf(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Nvm.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
}
