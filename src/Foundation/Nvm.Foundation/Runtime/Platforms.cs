using Microsoft.EntityFrameworkCore;

namespace Nvm.Foundation.Runtime;

/// <summary>Platform của pre-check: chỉ đọc. Không implement <see cref="IPlatform"/>, nên ép kiểu để ghi cũng không được.</summary>
internal class ReadOnlyPlatform(CommandExecutor executor, DomainTransaction transaction, ExecutionScope scope, CallTracer tracer)
    : IReadOnlyPlatform
{
    protected CommandExecutor Executor => executor;

    protected DomainTransaction Transaction => transaction;

    protected ExecutionScope Scope => scope;

    protected CallTracer CallTracer => tracer;

    public ITracer Tracer => tracer;

    public TimeProvider Time => executor.Time;

    public CallerIdentity Caller => scope.Caller;

    public IQueryable<T> Query<T>()
        where T : ModelObject
    {
        var domain = executor.Registry.DomainOf(typeof(T));
        if (domain != transaction.Domain)
        {
            throw new ModelMisuseException(
                $"Query<{typeof(T).Name}> reads {domain} inside a {transaction.Domain} transaction. Use ProjectionQuery.");
        }

        return transaction.Context.Set<T>();
    }

    public IQueryable<T> ProjectionQuery<T>()
        where T : ModelObject
    {
        _ = executor.Registry.DomainOf(typeof(T));
        return scope.Projection().Set<T>().AsNoTracking();
    }
}

/// <summary>Platform của handler và post-action.</summary>
internal sealed class Platform(CommandExecutor executor, DomainTransaction transaction, ExecutionScope scope, CallTracer tracer)
    : ReadOnlyPlatform(executor, transaction, scope, tracer), IPlatform
{
    public T Create<T>()
        where T : ModelObject, new()
    {
        _ = Executor.Registry.DomainOf(typeof(T));
        return new T { Id = Guid.NewGuid() };
    }

    public void Submit(ModelObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        RequireOwnDomain(item, nameof(Submit));
        var entry = Transaction.Context.Entry(item);
        if (entry.State == EntityState.Detached)
        {
            Transaction.Context.Add(item);
        }

        // Ghi xuống transaction ngay để Query của phần sau trong chuỗi thấy; commit vẫn đợi cuối chuỗi.
        Transaction.Context.SaveChanges();
    }

    public void Delete(ModelObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        RequireOwnDomain(item, nameof(Delete));
        Transaction.Context.SaveChanges();
        var table = Executor.Registry.TableOf(item.GetType());

        // Số đếm, không phải cờ: mỗi bản xóa một số riêng, nên unique index (IsDeleted, NId) vẫn cho tạo lại cùng NId.
        using var command = Transaction.Connection.CreateCommand();
        command.Transaction = Transaction.Transaction;
#pragma warning disable CA2100 // tên bảng lấy từ registry, không từ dữ liệu người dùng
        command.CommandText = $"""
            UPDATE dbo.[{table}]
            SET IsDeleted = (SELECT ISNULL(MAX(IsDeleted), 0) + 1 FROM dbo.[{table}] WITH (UPDLOCK, HOLDLOCK)),
                LastUpdatedOn = @now
            OUTPUT inserted.IsDeleted
            WHERE Id = @id AND IsDeleted = 0;
            """;
#pragma warning restore CA2100
        command.Parameters.AddWithValue("@id", item.Id);
        command.Parameters.AddWithValue("@now", Executor.Time.GetUtcNow());
        var marker = command.ExecuteScalar() as int?
            ?? throw new ModelMisuseException($"{item.GetType().Name} {item.Id} does not exist or is already deleted.");
        Transaction.Context.Entry(item).State = EntityState.Detached;
        item.IsDeleted = marker;
        CallTracer.Platform(TraceLevel.Verbose, $"Deleted {item.GetType().Name} {item.Id} (IsDeleted = {marker})");
    }

    public TResponse CallCommand<TCommand, TResponse>(TCommand command)
        where TCommand : ICommand<TResponse>
        where TResponse : CommandResponse, new()
    {
        ArgumentNullException.ThrowIfNull(command);
        return (TResponse)Executor.CallFromHandler(command, Transaction, Scope, CallTracer.Call);
    }

    public void RaiseEventOnCommit(IFoundationEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _ = Executor.Registry.EventOf(domainEvent.GetType());
        Transaction.AddEvent(domainEvent, CallTracer.Call);
        CallTracer.Platform(TraceLevel.Verbose, $"Event {domainEvent.GetType().Name} registered for commit");
    }

    public string? Endpoint(string name) => Executor.Solution.Endpoints.TryGetValue(name, out var value) ? value : null;

    private void RequireOwnDomain(ModelObject item, string operation)
    {
        var domain = Executor.Registry.DomainOf(item.GetType());
        if (domain != Transaction.Domain)
        {
            throw new ModelMisuseException(
                $"{operation} of {item.GetType().Name} ({domain}) inside a {Transaction.Domain} transaction. Call a command of that domain.");
        }
    }
}

/// <summary>Platform của composite command: không transaction, mỗi lời gọi đi qua đủ pipeline của app.</summary>
internal sealed class CompositePlatform(CommandExecutor executor, AppConfiguration app, ExecutionScope scope, CallTracer tracer)
    : ICompositePlatform
{
    public ITracer Tracer => tracer;

    public TimeProvider Time => executor.Time;

    public CallerIdentity Caller => scope.Caller;

    public TResponse CallCommand<TCommand, TResponse>(TCommand command)
        where TCommand : ICommand<TResponse>
        where TResponse : CommandResponse, new()
    {
        ArgumentNullException.ThrowIfNull(command);
        return (TResponse)executor.CallFromComposite(command, app, scope, tracer.Call);
    }

    public IQueryable<T> ProjectionQuery<T>()
        where T : ModelObject
    {
        _ = executor.Registry.DomainOf(typeof(T));
        return scope.Projection().Set<T>().AsNoTracking();
    }
}
