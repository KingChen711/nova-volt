using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Nvm.Foundation.Runtime;

/// <summary>Transaction của một chuỗi command trong một domain: một kết nối, một DbContext, các event chờ commit.</summary>
internal sealed class DomainTransaction : IDisposable
{
    private readonly List<(IFoundationEvent Event, CallInfo Call)> _events = [];
    private readonly ModelRegistry _registry;
    private readonly TimeProvider _time;
    private bool _finished;

    public DomainTransaction(string connectionString, ModelRegistry registry, TimeProvider time, ModelDomain domain)
    {
        _registry = registry;
        _time = time;
        Domain = domain;
        Connection = new SqlConnection(connectionString);
        Connection.Open();
        Transaction = Connection.BeginTransaction(IsolationLevel.ReadCommitted);
        Context = FoundationDbContext.Create(Connection, registry, time, tracking: true);
        Context.Database.UseTransaction(Transaction);
    }

    public ModelDomain Domain { get; }

    public SqlConnection Connection { get; }

    public SqlTransaction Transaction { get; }

    public FoundationDbContext Context { get; }

    /// <summary>Lỗi của command con đã hủy transaction (chỉ khi bật <see cref="SolutionConfiguration.ChildFailureDoomsTransaction"/>).</summary>
    public ExecutionError? DoomedBy { get; private set; }

    public void Doom(ExecutionError error) => DoomedBy ??= error;

    public void AddEvent(IFoundationEvent domainEvent, CallInfo call) => _events.Add((domainEvent, call));

    /// <summary>Ghi phần còn lại, lưu event vào cùng transaction rồi commit. Trả event để phát sau commit.</summary>
    public IReadOnlyList<PublishedEvent> Commit()
    {
        Context.SaveChanges();
        var published = new List<PublishedEvent>(_events.Count);
        foreach (var (domainEvent, call) in _events)
        {
            var model = _registry.EventOf(domainEvent.GetType());
            var record = new PublishedEvent(Guid.NewGuid(), model.Name, model.Version,
                JsonSerializer.Serialize(domainEvent, domainEvent.GetType()), call.CorrelationId, domainEvent);
            using var insert = Connection.CreateCommand();
            insert.Transaction = Transaction;
            insert.CommandText = $"""
                INSERT INTO dbo.{SchemaUpdater.EventTable} (EventId, EventType, EventVersion, Payload, CorrelationId, RaisedOn)
                VALUES (@id, @type, @version, @payload, @correlation, @raised);
                """;
            insert.Parameters.AddWithValue("@id", record.EventId);
            insert.Parameters.AddWithValue("@type", record.EventType);
            insert.Parameters.AddWithValue("@version", record.Version);
            insert.Parameters.AddWithValue("@payload", record.Payload);
            insert.Parameters.AddWithValue("@correlation", record.CorrelationId);
            insert.Parameters.AddWithValue("@raised", _time.GetUtcNow());
            insert.ExecuteNonQuery();
            published.Add(record);
        }

        Transaction.Commit();
        _finished = true;
        return published;
    }

    public void Rollback()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            Transaction.Rollback();
        }
        catch (InvalidOperationException)
        {
            // SQL Server đã tự hủy transaction (vd lỗi nghiêm trọng lúc commit): không còn gì để rollback.
        }
    }

    public void Dispose()
    {
        Rollback();
        Context.Dispose();
        Transaction.Dispose();
        Connection.Dispose();
    }
}

/// <summary>Tài nguyên sống suốt một lời gọi từ service layer: người gọi, kết nối đọc ngoài transaction của ProjectionQuery.</summary>
internal sealed class ExecutionScope(string connectionString, ModelRegistry registry, TimeProvider time, CallerIdentity caller)
    : IDisposable
{
    public CallerIdentity Caller => caller;

    private SqlConnection? _connection;
    private FoundationDbContext? _projection;

    public FoundationDbContext Projection()
    {
        if (_projection is null)
        {
            _connection = new SqlConnection(connectionString);
            _connection.Open();
            _projection = FoundationDbContext.Create(_connection, registry, time, tracking: false);
        }

        return _projection;
    }

    public void Dispose()
    {
        _projection?.Dispose();
        _connection?.Dispose();
    }
}
