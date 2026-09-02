using Microsoft.CodeAnalysis;

namespace Nvm.Analyzers;

/// <summary>Vài cái tên từ <c>Nvm.Contracts</c> mà các analyzer phải nhận ra.</summary>
/// <remarks>
/// <para>
/// Một analyzer không thể reference assembly nó đang phân tích — nó được compiler nạp lên, và
/// compiler đang build chính assembly đó. Nên mối liên kết này là theo tên, resolve theo từng
/// compilation.
/// </para>
/// <para>
/// Điều đó khiến các chuỗi này trở thành một coupling thật sự: đổi tên <c>IDomainEvent</c> hoặc
/// chuyển nó sang namespace khác sẽ tắt cả hai rule, và triệu chứng là một build xanh. Architecture
/// test ở C17 là thứ nhận ra điều đó, vì nó assert rằng type tồn tại đúng nơi các chuỗi này nói.
/// </para>
/// </remarks>
internal static class ContractSymbols
{
    internal const string DomainEventInterface = "Nvm.Contracts.Events.IDomainEvent";

    internal const string EventVersionAttribute = "Nvm.Contracts.Events.EventVersionAttribute";

    /// <summary>Assembly nơi mọi wire contract sinh sống.</summary>
    /// <remarks>
    /// NVM002 áp dụng cho toàn bộ assembly này, không chỉ cho event. Một record hôm nay chưa phải
    /// event sẽ trở thành payload của một event vào ngày mai, và đến lúc đó <c>DateTime</c> bên trong
    /// nó đã được serialize vào store rồi.
    /// </remarks>
    internal const string ContractsAssembly = "Nvm.Contracts";

    /// <summary>Một type có phải một concrete event hay không, khác với marker hay một abstract base.</summary>
    internal static bool IsConcreteDomainEvent(INamedTypeSymbol type, INamedTypeSymbol? domainEvent) =>
        domainEvent is not null
        && type is { IsAbstract: false, TypeKind: TypeKind.Class or TypeKind.Struct }
        && type.AllInterfaces.Contains(domainEvent, SymbolEqualityComparer.Default);

    /// <summary>Một type có thuộc về wire contract surface hay không.</summary>
    /// <remarks>
    /// Ba phép kiểm tra, OR với nhau, vì mỗi cái bắt được điều mà những cái khác bỏ lỡ: một type ở
    /// assembly khác implement marker; một type bên trong project contracts nhưng đặt ở namespace
    /// khác; và một type có hình dạng contract nhưng chưa được biến thành event. Cái cuối cùng là cái
    /// phổ biến nhất — một record hôm nay chưa phải event sẽ trở thành payload của một event ở
    /// milestone kế tiếp.
    /// </remarks>
    internal static bool IsWireContract(INamedTypeSymbol type, INamedTypeSymbol? domainEvent, bool assemblyIsContracts) =>
        IsConcreteDomainEvent(type, domainEvent)
        || assemblyIsContracts
        || IsInContractsNamespace(type);

    private static bool IsInContractsNamespace(INamedTypeSymbol type)
    {
        var name = type.ContainingNamespace?.ToDisplayString();

        return name is not null
            && (string.Equals(name, ContractsAssembly, StringComparison.Ordinal)
                || name.StartsWith(ContractsAssembly + ".", StringComparison.Ordinal));
    }
}
