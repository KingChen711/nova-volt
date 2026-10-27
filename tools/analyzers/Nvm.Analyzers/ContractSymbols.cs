using Microsoft.CodeAnalysis;

namespace Nvm.Analyzers;

/// <summary>Vài cái tên từ <c>Nvm.Foundation</c> mà các analyzer phải nhận ra.</summary>
/// <remarks>
/// <para>
/// Một analyzer không thể reference assembly nó đang phân tích — nó được compiler nạp lên, và
/// compiler đang build chính assembly đó. Nên mối liên kết này là theo tên, resolve theo từng
/// compilation.
/// </para>
/// <para>
/// Điều đó khiến các chuỗi này trở thành một coupling thật sự: đổi tên một type ở Foundation sẽ tắt
/// rule, và triệu chứng là một build xanh. Test trong <c>Nvm.FoundationTests</c> chứa đúng một chỗ dùng
/// sai model có <c>#pragma warning disable NVM004</c>; rule im lặng thì pragma đó vô nghĩa nhưng không đỏ,
/// nên test analyzer dựng snippet bằng đúng các tên dưới đây.
/// </para>
/// </remarks>
internal static class ContractSymbols
{
    internal const string FoundationEvent = "Nvm.Foundation.IFoundationEvent";

    internal const string EventVersionAttribute = "Nvm.Foundation.EventVersionAttribute";

    /// <summary>Gốc của mọi entity và facet: cột của chúng lưu thẳng xuống DB MOM (K2).</summary>
    internal const string ModelObject = "Nvm.Foundation.ModelObject";

    internal const string ReadOnlyPlatform = "Nvm.Foundation.IReadOnlyPlatform";

    internal const string FunctionalBlockAttribute = "Nvm.Foundation.FunctionalBlockAttribute";

    internal const string EntityAttribute = "Nvm.Foundation.EntityAttribute`1";

    internal const string FacetAttribute = "Nvm.Foundation.FacetAttribute`1";

    internal const string CommandAttribute = "Nvm.Foundation.CommandAttribute`1";

    internal const string CommandHandler = "Nvm.Foundation.ICommandHandler`2";

    internal const string PreCheck = "Nvm.Foundation.IPreCheck`1";

    internal const string PostAction = "Nvm.Foundation.IPostAction`2";

    /// <summary>Một type có phải một concrete event hay không, khác với marker hay một abstract base.</summary>
    internal static bool IsConcreteEvent(INamedTypeSymbol type, INamedTypeSymbol? foundationEvent) =>
        foundationEvent is not null
        && type is { IsAbstract: false, TypeKind: TypeKind.Class or TypeKind.Struct }
        && type.AllInterfaces.Contains(foundationEvent, SymbolEqualityComparer.Default);

    /// <summary>Type mà dữ liệu của nó ra khỏi process: event (lên bus, vào bảng event) hoặc entity/facet (vào bảng).</summary>
    internal static bool IsPersistedContract(INamedTypeSymbol type, INamedTypeSymbol? foundationEvent, INamedTypeSymbol? modelObject) =>
        IsConcreteEvent(type, foundationEvent) || (modelObject is not null && DerivesFrom(type, modelObject));

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }
}
