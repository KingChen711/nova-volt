using Microsoft.CodeAnalysis.Testing;
using Nvm.Analyzers;

namespace Nvm.AnalyzerTests;

public sealed class Nvm004CrossDomainQueryAnalyzerTests
{
    private static Task VerifyAsync(string source, params DiagnosticResult[] expected) =>
        AnalyzerSnippet.VerifyAsync<Nvm004CrossDomainQueryAnalyzer>(source, expected);

    private static DiagnosticResult Violation(string source, string expression, string target, string targetDomain, string commandDomain, string command) =>
        AnalyzerSnippet.Violation(source, expression, Nvm004CrossDomainQueryAnalyzer.DiagnosticId, target, targetDomain, commandDomain, command);

    // Một block master data, một block vận hành, và một lớp xử lý command vận hành với thân do test điền.
    private static string Model(string handler) =>
        $$"""
        {{AnalyzerSnippet.ContractTypes}}

        namespace Somewhere.Else
        {
            using System.Linq;
            using Nvm.Foundation;

            [FunctionalBlock("FB_MS_Probe", ModelDomain.MasterData)]
            public sealed class MasterBlock { }

            [FunctionalBlock("FB_OP_Probe", ModelDomain.OperationalData)]
            public sealed class OperationBlock { }

            [Entity<MasterBlock>]
            public sealed class Material : Entity { }

            [Entity<OperationBlock>]
            public sealed class Order : Entity { }

            [Command<OperationBlock>]
            public sealed class StartOrder { public string NId { get; set; } = ""; }

            {{handler}}
        }
        """;

    [Fact]
    public async Task QueryOfAnotherDomainInAHandlerIsRefused()
    {
        var source = Model("""
            public sealed class StartOrderHandler : ICommandHandler<StartOrder, CommandResponse>
            {
                public CommandResponse Handle(StartOrder command, IPlatform platform)
                {
                    _ = platform.Query<Material>().Count();
                    return new CommandResponse();
                }
            }
            """);

        await VerifyAsync(source,
            Violation(source, "platform.Query<Material>()", "Material", "MasterData", "OperationalData", "StartOrder"));
    }

    [Fact]
    public async Task QueryOfTheSameDomainStaysSilent()
    {
        // Đối chứng: không có nó, một rule báo mọi Query vẫn pass test ở trên.
        await VerifyAsync(Model("""
            public sealed class StartOrderHandler : ICommandHandler<StartOrder, CommandResponse>
            {
                public CommandResponse Handle(StartOrder command, IPlatform platform)
                {
                    _ = platform.Query<Order>().Count();
                    return new CommandResponse();
                }
            }
            """));
    }

    [Fact]
    public async Task ProjectionQueryOfAnotherDomainIsTheSanctionedWay()
    {
        await VerifyAsync(Model("""
            public sealed class StartOrderHandler : ICommandHandler<StartOrder, CommandResponse>
            {
                public CommandResponse Handle(StartOrder command, IPlatform platform)
                {
                    _ = platform.ProjectionQuery<Material>().Count();
                    return new CommandResponse();
                }
            }
            """));
    }

    [Fact]
    public async Task PreCheckIsHeldToTheSameRule()
    {
        // Pre-check đọc trong transaction của command nó gắn vào, nên cũng chỉ một domain.
        var source = Model("""
            public sealed class StartOrderCheck : IPreCheck<StartOrder>
            {
                public bool Check(StartOrder command, IReadOnlyPlatform platform) => platform.Query<Material>().Any();
            }
            """);

        await VerifyAsync(source,
            Violation(source, "platform.Query<Material>()", "Material", "MasterData", "OperationalData", "StartOrder"));
    }

    [Fact]
    public async Task CodeOutsideACommandIsNotJudged()
    {
        // Không biết domain của lời gọi thì không đoán; runtime vẫn chặn bằng ModelMisuseException.
        await VerifyAsync(Model("""
            public static class Helper
            {
                public static int Count(IPlatform platform) => platform.Query<Material>().Count();
            }
            """));
    }
}
