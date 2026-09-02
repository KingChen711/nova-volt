using System.Reflection;
using NetArchTest.Rules;

namespace Nvm.ArchitectureTests;

/// <summary>A1, A2 — những gì tầng Platform được phép biết đến.</summary>
/// <remarks>
/// Mọi rule ở đây đều đi kèm một <b>positive control</b>: cùng một kiểm tra áp dụng lên một assembly
/// lẽ ra phải fail nó. Không có control, một rule kiểm tra sai thứ — một danh sách type rỗng, một
/// namespace prefix viết sai — sẽ báo "không tìm thấy vi phạm" và mãi mãi xanh. Đó chính là phiên bản
/// architecture-test của cái analyzer im lặng ở C15.1.
/// </remarks>
public sealed class PlatformBoundaryTests
{
    /// <summary>Infrastructure mà domain code không được phép chạm tới. AGENTS.md K9.</summary>
    private static readonly string[] Infrastructure =
    [
        "MassTransit",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Microsoft.Data.SqlClient",
    ];

    [Fact]
    public void A1_Contracts_ReferencesNothingButTheBcl()
    {
        // Nvm.Contracts là đáy của cây dependency, và nó là assembly mà một Mendix developer hay một
        // consumer bên ngoài sẽ được trao. Một package reference ở đây trở thành package reference
        // cho tất cả mọi thứ downstream, mãi mãi.
        var outsiders = NvmAssemblies.Contracts
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !NvmAssemblies.IsBcl(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        outsiders.ShouldBeEmpty(
            $"Nvm.Contracts must stay BCL-only; it references {string.Join(", ", outsiders)}");
    }

    [Fact]
    public void A1_Control_TheBclFilterCanActuallySeeANonBclReference()
    {
        // Nếu IsBcl trả lời "có" cho mọi thứ, hoặc GetReferencedAssemblies trả về rỗng, A1 ở trên sẽ
        // pass trong khi không kiểm tra gì cả.
        NvmAssemblies.Bus
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !NvmAssemblies.IsBcl(name))
            .ShouldNotBeEmpty();
    }

    [Fact]
    public void A2_Kernel_DoesNotKnowAboutTransportOrDatabases()
    {
        // Command pipeline phải test được mà không cần broker hay database. Nó có reference
        // Microsoft.Extensions.DependencyInjection.Abstractions, và đó không phải một vi phạm: K9
        // cấm infrastructure, không cấm khả năng đăng ký một handler.
        var result = Types.InAssembly(NvmAssemblies.Kernel)
            .ShouldNot()
            .HaveDependencyOnAny(Infrastructure)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Nvm.Kernel depends on infrastructure through: {Describe(result)}");
    }

    [Fact]
    public void A2_Control_TheDependencyCheckCanActuallyFindMassTransit()
    {
        // Nvm.Bus phụ thuộc vào MassTransit theo thiết kế. Nếu cái này pass, HaveDependencyOnAny
        // không nhìn vào nơi nó tuyên bố đang nhìn, và A2 ở trên chẳng chứng minh được gì.
        Types.InAssembly(NvmAssemblies.Bus)
            .ShouldNot()
            .HaveDependencyOnAny("MassTransit")
            .GetResult()
            .IsSuccessful.ShouldBeFalse();
    }

    [Fact]
    public void A8_Time_ReferencesNothingButTheBcl()
    {
        // Production calendar là một hàm domain, không phải một query. Một package reference ở đây —
        // một ORM, một client, một serializer — và một DST test sẽ cần infrastructure để chạy, đó
        // chính xác là cách hai ngày trong năm thực sự quan trọng ngừng được kiểm thử.
        var outsiders = NvmAssemblies.Time
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !NvmAssemblies.IsBcl(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        outsiders.ShouldBeEmpty(
            $"Nvm.Time must stay BCL-only; it references {string.Join(", ", outsiders)}");
    }

    [Fact]
    public void A8_Time_DoesNotReachBackIntoAFunctionalBlock()
    {
        // Dependency chỉ chạy theo một chiều: Nvm.FactoryModel implement ISiteCalendarDirectory vì nó
        // sở hữu cây plant. Đảo ngược chiều này sẽ đặt một bounded context xuống dưới một tầng
        // Platform và khiến calendar không thể test được nếu thiếu seed file.
        NvmAssemblies.NvmReferencesOf(NvmAssemblies.Time).ShouldBeEmpty();
    }

    [Fact]
    public void A8_Control_TheFactoryModelReallyDoesAnswerTheCalendarQuestion()
    {
        // Bảo vệ trường hợp A8 pass vì cạnh nối đó không hề tồn tại: nếu không gì implement
        // ISiteCalendarDirectory, "Nvm.Time không reference block nào" sẽ đúng nhưng vô nghĩa. Adapter
        // phải ở đó, trong block, trỏ theo chiều này.
        NvmAssemblies.NvmReferencesOf(NvmAssemblies.FactoryModel).ShouldContain("Nvm.Time");
    }

    [Fact]
    public void A7_TheGeneratedSparkplugTypesDoNotLeaveNvmSparkplug()
    {
        // ADR-026 vendor hóa sparkplug_b.proto và sinh C# từ đó, nghĩa là Org.Eclipse.Tahu.* là một
        // hình dạng mà repository này không kiểm soát. Một trong các type đó xuất hiện trong một
        // public signature khiến mọi caller phụ thuộc vào một file mà chúng ta không được phép sửa,
        // và ngày spec thay đổi thì thay đổi đó ập đến khắp nơi cùng một lúc.
        var leaks = NvmAssemblies.Sparkplug
            .GetExportedTypes()
            .Where(type => string.Equals(type.Namespace, "Nvm.Sparkplug", StringComparison.Ordinal))
            .SelectMany(MembersExposingGeneratedTypes)
            .Order(StringComparer.Ordinal)
            .ToList();

        leaks.ShouldBeEmpty(
            $"generated Sparkplug types reach callers through: {string.Join(", ", leaks)}");
    }

    [Fact]
    public void A7_Control_TheGeneratedTypeWalkCanActuallyFindOne()
    {
        // Áp dụng lên một generated type, cùng một lượt duyệt phải trả về đầy đủ. Không có cái này,
        // một namespace viết sai trong filter hoặc predicate sẽ báo "không rò rỉ" và mãi mãi xanh.
        var generated = NvmAssemblies.Sparkplug.GetType("Org.Eclipse.Tahu.Protobuf.Payload", throwOnError: true)!;

        MembersExposingGeneratedTypes(generated).ShouldNotBeEmpty();
    }

    [Fact]
    public void A7_OnlyNvmSparkplugKnowsThereIsProtobufAtAll()
    {
        // Nửa còn lại ở mức reference. A6 ngăn một Functional Block tự chọn transport của riêng nó;
        // cái này ngăn nó tự chọn device codec của riêng nó — C08 và C12 tiêu thụ reading, không phải
        // payload.
        foreach (var assembly in new[]
        {
            NvmAssemblies.Contracts,
            NvmAssemblies.Kernel,
            NvmAssemblies.Bus,
            NvmAssemblies.FactoryModel,
        })
        {
            NvmAssemblies.NamesReferencedBy(assembly).ShouldNotContain(
                "Google.Protobuf",
                $"{assembly.GetName().Name} references Google.Protobuf; decoding belongs to Nvm.Sparkplug");
        }

        // Kiêm luôn vai trò control: assembly duy nhất lẽ ra phải reference nó, thì có reference thật.
        NvmAssemblies.NamesReferencedBy(NvmAssemblies.Sparkplug).ShouldContain("Google.Protobuf");
    }

    /// <summary>Nêu tên các public member của một type mà signature của nó nhắc tới một generated type.</summary>
    private static IEnumerable<string> MembersExposingGeneratedTypes(Type type)
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        foreach (var property in type.GetProperties(Public).Where(p => IsGenerated(p.PropertyType)))
        {
            yield return $"{type.Name}.{property.Name}";
        }

        foreach (var method in type.GetMethods(Public | BindingFlags.DeclaredOnly))
        {
            if (IsGenerated(method.ReturnType))
            {
                yield return $"{type.Name}.{method.Name}() returns";
            }

            foreach (var parameter in method.GetParameters().Where(p => IsGenerated(p.ParameterType)))
            {
                yield return $"{type.Name}.{method.Name}({parameter.Name})";
            }
        }

        foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()).Where(p => IsGenerated(p.ParameterType)))
        {
            yield return $"{type.Name}..ctor({parameter.Name})";
        }
    }

    /// <summary>Một type có đến từ vendored schema hay không, dù lồng sâu đến đâu.</summary>
    /// <remarks>
    /// Phép đệ quy quan trọng hơn trường hợp trực tiếp: <c>MessageParser&lt;Payload&gt;</c> sống trong
    /// <c>Google.Protobuf</c> và sẽ pass một kiểm tra chỉ nhìn vào namespace ngoài cùng, trong khi vẫn
    /// trao cho caller một <c>Payload</c> y như vậy.
    /// </remarks>
    private static bool IsGenerated(Type type) =>
        (type.Namespace?.StartsWith("Org.Eclipse.Tahu", StringComparison.Ordinal) ?? false)
        || (type.IsArray && IsGenerated(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(IsGenerated));

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.FailingTypeNames is null
            ? "(no type names reported)"
            : string.Join(", ", result.FailingTypeNames);
}
