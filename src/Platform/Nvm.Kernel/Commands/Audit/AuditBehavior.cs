namespace Nvm.Kernel.Commands.Audit;

/// <summary>Records that a command was carried out, how long it took, and whether it worked.</summary>
/// <typeparam name="TCommand">The command being handled.</typeparam>
/// <typeparam name="TResult">What handling it yields.</typeparam>
/// <param name="sink">Where entries go.</param>
/// <param name="clock">The only clock. Never <c>DateTimeOffset.UtcNow</c> (AGENTS.md K1).</param>
/// <remarks>
/// <para>
/// Innermost of the three, so it wraps the handler and nothing else. That placement has a consequence
/// worth stating rather than discovering: a command rejected by validation, and a duplicate
/// short-circuited above, <b>do not appear in the trail</b>.
/// </para>
/// <para>
/// That is the intended reading of an audit trail here — a record of what the plant actually did to
/// its product, not a record of every request that arrived. A duplicate changed nothing, so it
/// changed nothing to record; it is counted as a metric instead. When electronic signatures arrive
/// and refused attempts become interesting in their own right, they get their own trail rather than
/// being mixed into this one.
/// </para>
/// <para>
/// Failures are recorded and then rethrown. A trail holding only successes cannot answer the question
/// an investigation actually starts with, which is what was tried and did not work.
/// </para>
/// </remarks>
public sealed class AuditBehavior<TCommand, TResult>(ICommandAuditSink sink, TimeProvider clock)
    : ICommandBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    private readonly ICommandAuditSink _sink = sink;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(
        TCommand command,
        CommandPipelineStep<TResult> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(continuation);

        var startedAt = _clock.GetUtcNow();

        // Two different clocks on purpose. GetUtcNow answers "when", and is the value an auditor reads.
        // GetTimestamp answers "how long", and is monotonic — it does not jump when NTP corrects the
        // machine, so a duration cannot come out negative.
        var startedTicks = _clock.GetTimestamp();

        try
        {
            var result = await continuation().ConfigureAwait(false);

            await WriteAsync(command, startedAt, startedTicks, failure: null, cancellationToken)
                .ConfigureAwait(false);

            return result;
        }
        catch (Exception failure)
        {
            // CancellationToken.None: the operation is being abandoned, and that is exactly when the
            // trail is worth having. Passing the cancelled token here would abandon the record too.
            await WriteAsync(command, startedAt, startedTicks, failure, CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
    }

    private Task WriteAsync(
        TCommand command,
        DateTimeOffset startedAt,
        long startedTicks,
        Exception? failure,
        CancellationToken cancellationToken) =>
        _sink.WriteAsync(
            new CommandAuditEntry(
                command.IdempotencyKey,
                typeof(TCommand).Name,
                startedAt,
                _clock.GetElapsedTime(startedTicks),
                failure is null,
                failure?.GetType().Name,
                failure?.Message),
            cancellationToken);
}
