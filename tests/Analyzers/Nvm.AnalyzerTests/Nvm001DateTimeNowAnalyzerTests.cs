using Microsoft.CodeAnalysis.Testing;
using Nvm.Analyzers;

namespace Nvm.AnalyzerTests;

public sealed class Nvm001DateTimeNowAnalyzerTests
{
    private static Task VerifyAsync(string source, params DiagnosticResult[] expected) =>
        AnalyzerSnippet.VerifyAsync<Nvm001DateTimeNowAnalyzer>(source, expected);

    private static DiagnosticResult Violation(string source, string expression, string type, string member) =>
        AnalyzerSnippet.Violation(source, expression, Nvm001DateTimeNowAnalyzer.DiagnosticId, type, member);

    [Theory]
    [InlineData("DateTime", "UtcNow")]
    [InlineData("DateTime", "Now")]
    [InlineData("DateTime", "Today")]
    [InlineData("DateTimeOffset", "UtcNow")]
    [InlineData("DateTimeOffset", "Now")]
    public async Task EveryAmbientClockMemberIsRefused(string type, string member)
    {
        // Cả năm cái, không chỉ mỗi cái mà ai cũng viết. Một analyzer chỉ bắt DateTime.UtcNow và
        // không gì khác sẽ đẩy vấn đề sang DateTimeOffset.Now, nơi nó khó phát hiện hơn vì trông như
        // được chọn có chủ đích.
        var expression = $"System.{type}.{member}";
        var source = $$"""
            class Probe
            {
                object Read() => {{expression}};
            }
            """;

        await VerifyAsync(source, Violation(source, expression, type, member));
    }

    [Fact]
    public async Task AnAliasDoesNotHideTheClock()
    {
        // Lý do analyzer này khớp theo symbol thay vì theo text. Một rule grep chuỗi
        // "DateTime.UtcNow" bị đánh bại bởi một using directive, và một rule dễ dàng né tránh dạy
        // người ta né nó thay vì sửa code. Lưu ý message vẫn nêu tên type thật, không phải alias —
        // nếu không người đọc sẽ đi tìm một type tên là Clock.
        const string source = """
            using Clock = System.DateTime;

            class Probe
            {
                object Read() => Clock.UtcNow;
            }
            """;

        await VerifyAsync(source, Violation(source, "Clock.UtcNow", "DateTime", "UtcNow"));
    }

    [Fact]
    public async Task TimeProviderIsTheSanctionedWayAndStaysSilent()
    {
        // Ca kiểm chứng đối chứng. Không có nó, một analyzer đánh dấu mọi property reference trong
        // codebase vẫn sẽ pass mọi test ở trên.
        await VerifyAsync(
            """
            class Probe
            {
                object Read(System.TimeProvider clock) => clock.GetUtcNow();
            }
            """);
    }

    [Fact]
    public async Task AnUnrelatedNowPropertyIsNotTheMachineClock()
    {
        // Cùng tên, khác nghĩa. Khớp chỉ dựa trên tên member sẽ từ chối một domain property hoàn toàn
        // hợp lệ và biến rule thành nhiễu mà người ta sẽ tắt đi.
        await VerifyAsync(
            """
            class Shift
            {
                public string Now => "A";
            }

            class Probe
            {
                object Read(Shift shift) => shift.Now;
            }
            """);
    }
}
