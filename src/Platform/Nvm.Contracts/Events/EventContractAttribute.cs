namespace Nvm.Contracts.Events;

/// <summary>Khai báo wire name của một domain event: bounded context và tên event của nó.</summary>
/// <param name="context">Bounded context sở hữu event, ví dụ <c>factory-model</c>.</param>
/// <param name="name">Tên event ở dạng kebab-case, ví dụ <c>revision-activated</c>.</param>
/// <remarks>
/// <para>
/// Khai báo tường minh thay vì suy ra. Phương án hiển nhiên hơn là dựng wire name từ type và
/// namespace C# — <c>Nvm.Contracts.Events.FactoryModel.FactoryModelRevisionActivated</c> có thể được
/// gộp thành cùng một chuỗi một cách tự động. Điều đó sẽ biến một lần rename trong IDE thành một thay
/// đổi contract: exchange dịch chuyển, các binding hiện có không còn khớp, và không ai được cảnh báo.
/// Wire name là một sự thật vận hành, và nó thuộc về source code đúng như bản chất đó.
/// </para>
/// <para>
/// Cùng với <see cref="EventVersionAttribute"/>, đây là tất cả những gì một
/// <see cref="CloudEvents.EventTypeName"/> cần. Hai attribute tách riêng vì chúng thay đổi vì những lý
/// do khác nhau: version dịch chuyển khi schema thay đổi, còn name thì không bao giờ dịch chuyển.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EventContractAttribute(string context, string name) : Attribute
{
    /// <summary>Bounded context sở hữu event.</summary>
    public string Context { get; } = context;

    /// <summary>Tên event, ở dạng kebab-case.</summary>
    public string Name { get; } = name;
}
