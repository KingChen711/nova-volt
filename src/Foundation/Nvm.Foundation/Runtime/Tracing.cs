using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nvm.Foundation.Runtime;

/// <summary>Một dòng trace, cùng các cột mà Trace Viewer của nền thật hiện sẵn.</summary>
/// <remarks>
/// Lời gọi từ service layer: <c>CorrelationId</c> = <c>CurrentId</c> = <c>RootRequestId</c>, <c>ParentId</c> rỗng. Command con
/// có <c>CurrentId</c> mới, <c>ParentId</c> trỏ về lời gọi cha, giữ <c>CorrelationId</c> và <c>RootRequestId</c>.
/// </remarks>
public sealed record TraceRecord(
    DateTimeOffset Timestamp,
    string Provider,
    TraceLevel Level,
    Guid CorrelationId,
    Guid CurrentId,
    Guid? ParentId,
    Guid RootRequestId,
    string ContextName,
    string Message);

public interface ITraceSink
{
    void Write(TraceRecord record);
}

/// <summary>Giữ trace trong bộ nhớ; test và bộ chẩn đoán lọc theo <c>CorrelationId</c>.</summary>
public sealed class InMemoryTraceSink : ITraceSink
{
    private readonly ConcurrentQueue<TraceRecord> _records = new();

    public IReadOnlyList<TraceRecord> Records => [.. _records];

    public IReadOnlyList<TraceRecord> ByCorrelation(Guid correlationId) =>
        [.. _records.Where(r => r.CorrelationId == correlationId)];

    public void Write(TraceRecord record) => _records.Enqueue(record);
}

/// <summary>Vị trí của một lời gọi trong chuỗi.</summary>
internal sealed record CallInfo(Guid CorrelationId, Guid CurrentId, Guid? ParentId, Guid RootRequestId)
{
    public static CallInfo Root()
    {
        var id = Guid.NewGuid();
        return new CallInfo(id, id, null, id);
    }

    public CallInfo Child() => new(CorrelationId, Guid.NewGuid(), CurrentId, RootRequestId);
}

/// <summary>Ghi trace của một lời gọi: dòng của platform và dòng của handler cùng mang vị trí của lời gọi.</summary>
internal sealed class CallTracer(ITraceSink sink, TimeProvider time, CallInfo call, string contextName) : ITracer
{
    internal const string PlatformProvider = "Nvm-Foundation-Platform";
    internal const string BusinessProvider = "Nvm-Foundation-BusinessLogic";

    internal static readonly ActivitySource Source = new("Nvm.Foundation");

    public CallInfo Call => call;

    public void Write(TraceLevel level, string message) => Emit(BusinessProvider, level, message);

    public void Platform(TraceLevel level, string message) => Emit(PlatformProvider, level, message);

    private void Emit(string provider, TraceLevel level, string message)
    {
        sink.Write(new TraceRecord(time.GetUtcNow(), provider, level, call.CorrelationId, call.CurrentId, call.ParentId,
            call.RootRequestId, contextName, message));
        Activity.Current?.AddEvent(new ActivityEvent(message));
    }
}
