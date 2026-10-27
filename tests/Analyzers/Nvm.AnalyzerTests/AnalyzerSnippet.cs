using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace Nvm.AnalyzerTests;

/// <summary>Compile một snippet với một analyzer và kiểm tra chính xác diagnostic nào xuất ra.</summary>
/// <remarks>
/// <para>
/// <c>DefaultVerifier</c>, không phải <c>XUnitVerifier</c>: flavour <c>.XUnit</c> của testing package
/// được xây trên xunit v2 và repo này chỉ dùng v3. Một assertion thất bại vẫn throw và vẫn khiến test
/// fail — cái mất đi là định dạng failure kiểu xunit, không đáng để đổi lấy một test framework thứ hai.
/// </para>
/// <para>
/// Các span mong đợi được <b>tìm ra trong source</b> thay vì viết tay. Số dòng và số cột đếm bằng tay
/// sai ngay từ lần đầu và lại sai sau bất kỳ chỉnh sửa nào phía trên chúng, và failure chúng tạo ra
/// đọc y hệt như analyzer bị hỏng.
/// </para>
/// </remarks>
internal static class AnalyzerSnippet
{
    /// <summary>Bản rút gọn các type của <c>Nvm.Foundation</c> mà analyzer nhận ra, vì snippet không reference project nào.</summary>
    /// <remarks>
    /// Khai báo ở đây thay vì reference, và đó là có chủ đích: nó thực thi đúng cái sự thật rằng các
    /// analyzer resolve các type này <b>theo tên</b>. Đổi tên một trong số chúng thật sự và các
    /// rule sẽ im lặng — một snippet lấy chúng từ project reference sẽ không bao giờ nhận ra.
    /// </remarks>
    public const string ContractTypes = """
        namespace Nvm.Foundation
        {
            public enum ModelDomain { MasterData = 1, OperationalData = 2, ReferenceData = 3 }

            public sealed class FunctionalBlockAttribute : System.Attribute
            {
                public FunctionalBlockAttribute(string name, ModelDomain domain) { }
            }

            public sealed class EntityAttribute<TBlock> : System.Attribute where TBlock : class { }

            public sealed class FacetAttribute<TBlock> : System.Attribute where TBlock : class { }

            public sealed class CommandAttribute<TBlock> : System.Attribute where TBlock : class { }

            public interface IFoundationEvent { }

            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false)]
            public sealed class EventVersionAttribute : System.Attribute
            {
                public EventVersionAttribute(int version) => Version = version;

                public int Version { get; }
            }

            public abstract class ModelObject { public System.Guid Id { get; set; } }

            public abstract class Entity : ModelObject { public string NId { get; set; } = ""; }

            public interface IReadOnlyPlatform
            {
                System.Linq.IQueryable<T> Query<T>() where T : ModelObject;

                System.Linq.IQueryable<T> ProjectionQuery<T>() where T : ModelObject;
            }

            public interface IPlatform : IReadOnlyPlatform { }

            public class CommandResponse { }

            public interface ICommandHandler<in TCommand, out TResponse> { TResponse Handle(TCommand command, IPlatform platform); }

            public interface IPreCheck<in TCommand> { bool Check(TCommand command, IReadOnlyPlatform platform); }

            public interface IPostAction<in TCommand, TResponse> { TResponse Execute(TCommand command, TResponse response, IPlatform platform); }
        }
        """;

    public static async Task VerifyAsync<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source,

            // Các snippet nhắc tới System.TimeProvider và System.Collections.Generic, mà bộ reference
            // mặc định của package không mang theo.
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };

        test.ExpectedDiagnostics.AddRange(expected);

        await test.RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Mong đợi <paramref name="id"/> trên lần xuất hiện đầu tiên của <paramref name="expression"/>.</summary>
    public static DiagnosticResult Violation(string source, string expression, string id, params object[] arguments)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var line = Array.FindIndex(lines, text => text.Contains(expression, StringComparison.Ordinal));

        if (line < 0)
        {
            throw new ArgumentException($"'{expression}' is not in the snippet.", nameof(expression));
        }

        var column = lines[line].IndexOf(expression, StringComparison.Ordinal);

        return new DiagnosticResult(id, DiagnosticSeverity.Error)
            .WithSpan(line + 1, column + 1, line + 1, column + 1 + expression.Length)
            .WithArguments(arguments);
    }
}
