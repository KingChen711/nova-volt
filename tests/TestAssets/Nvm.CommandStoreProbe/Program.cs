using System.Data;
using System.Text.Json;
using Nvm.CommandStore;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Idempotency;

// Process độc lập cho test restart/crash; credential chỉ nhận qua environment và không được in.
try
{
    var mode = args[0];
    var submission = args[1];
    var command = new ProbeCommand(submission);
    await using var session = new SqlCommandSession();
    var memory = new InMemoryIdempotencyStore(TimeProvider.System);
    IIdempotencyStore store = mode == "legacy" ? memory : new SqlIdempotencyStore(session,
        new SqlCommandStoreOptions { ConnectionString = Environment.GetEnvironmentVariable("NVM_PROBE_SQL")! }, memory);
    var behavior = new IdempotencyBehavior<ProbeCommand, int>(store, TimeProvider.System);
    var handled = false;
    var result = await behavior.HandleAsync(command, async () =>
    {
        handled = true;
        if (mode != "legacy")
        {
            using var insert = session.CreateCommand("""
                INSERT INTO execution.DataCollectionTest(SiteId, IdempotencyKey, Payload)
                VALUES (@s, @k, 'probe');
                """);
            insert.Parameters.Add("@s", SqlDbType.VarChar, 3).Value = command.SiteId;
            insert.Parameters.Add("@k", SqlDbType.UniqueIdentifier).Value = command.IdempotencyKey.Value;
            await insert.ExecuteNonQueryAsync();
        }
        if (mode == "crash")
        {
            Console.WriteLine("effect-written");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        return 17;
    }, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { Result = result, Handled = handled, ProcessId = Environment.ProcessId }));
    return 0;
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(exception.GetType().Name);
    return 1;
}

internal sealed record ProbeCommand(string SubmissionId) : IDurableCommand, ICommand<int>
{
    public string CommandType => "ProbeCommand";
    public string SiteId => "NV1";
    public string ActorId => "probe";
    public string CanonicalPayload => "probe";
    public IdempotencyKey IdempotencyKey => IdempotencyKey.FromNaturalKey(SiteId, nameof(ProbeCommand), SubmissionId);
}
