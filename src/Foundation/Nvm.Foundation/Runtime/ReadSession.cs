using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Nvm.Foundation.Runtime;

/// <summary>Phiên đọc của service layer: entity set và reading function của app, ngoài transaction.</summary>
/// <remarks>
/// Query trả ra chưa chạy. OData áp <c>$filter</c>, <c>$top</c>… lên nó rồi mới đọc DB, trong lúc đã ghi phần đầu của
/// response; nên lỗi lúc đọc (vd view của reading model chưa cài) cho HTTP 200 với thân bị cắt, như nền thật (lab L15).
/// </remarks>
public sealed class ReadSession : IDisposable
{
    private static readonly MethodInfo SetMethod = typeof(ReadSession).GetMethod(nameof(SetOf))!;

    private readonly CommandExecutor _executor;
    private readonly ExecutionScope _scope;

    internal ReadSession(CommandExecutor executor, ExecutionScope scope)
    {
        _executor = executor;
        _scope = scope;
    }

    public CallerIdentity Caller => _scope.Caller;

    /// <summary>Entity, facet hoặc reading model; bản đã xóa mềm bị lọc.</summary>
    public IQueryable<T> SetOf<T>()
        where T : class
    {
        _ = _executor.Registry.DomainOf(typeof(T));
        return _scope.Projection().Set<T>().AsNoTracking();
    }

    public IQueryable Set(Type type) => (IQueryable)SetMethod.MakeGenericMethod(type).Invoke(this, null)!;

    /// <summary>Gọi reading function với tham số JSON; trả query trì hoãn.</summary>
    public IQueryable Invoke(ReadingFunctionModel function, JsonElement parameters)
    {
        ArgumentNullException.ThrowIfNull(function);
        var args = parameters.ValueKind == JsonValueKind.Undefined
            ? Activator.CreateInstance(function.ParametersType)
            : parameters.Deserialize(function.ParametersType, CommandExecutor.RequestJson);
        var instance = Activator.CreateInstance(function.Type)!;
        return (IQueryable)typeof(IReadingFunction<,>).MakeGenericType(function.ParametersType, function.RowType)
            .GetMethod("Read")!
            .Invoke(instance, BindingFlags.DoNotWrapExceptions, null,
                [args ?? Activator.CreateInstance(function.ParametersType), new ReadingPlatform(this, _executor.Time)],
                CultureInfo.InvariantCulture)!;
    }

    public void Dispose() => _scope.Dispose();

    private sealed class ReadingPlatform(ReadSession session, TimeProvider time) : IReadingPlatform
    {
        public CallerIdentity Caller => session.Caller;

        public TimeProvider Time => time;

        public IQueryable<T> ProjectionQuery<T>()
            where T : class => session.SetOf<T>();
    }
}
