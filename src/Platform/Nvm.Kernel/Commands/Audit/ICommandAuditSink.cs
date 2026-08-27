namespace Nvm.Kernel.Commands.Audit;

/// <summary>One line of the audit trail: a command was attempted, and this is what came of it.</summary>
/// <param name="IdempotencyKey">Which intention this was, and the join back to the event it produced.</param>
/// <param name="CommandType">Name of the command.</param>
/// <param name="StartedAt">When handling began, from the ambient clock.</param>
/// <param name="Duration">How long it took. A step that normally takes 40 ms and now takes 4 s is a symptom.</param>
/// <param name="Succeeded">Whether the handler completed.</param>
/// <param name="FailureType">Name of the exception when it did not, otherwise null.</param>
/// <param name="FailureMessage">Why it failed, otherwise null.</param>
public sealed record CommandAuditEntry(
    IdempotencyKey IdempotencyKey,
    string CommandType,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    bool Succeeded,
    string? FailureType,
    string? FailureMessage);

/// <summary>Where audit entries go.</summary>
/// <remarks>
/// <para>
/// An audit trail is not a log. A log is for whoever is debugging today and may be sampled, rotated
/// or turned off; an audit trail is a record of what the plant did, read by an auditor years later,
/// and IATF 16949 expects it to still be there. Same words, different obligations — which is why this
/// is its own abstraction and not a logger call.
/// </para>
/// <para>
/// <b>Known gap:</b> there is no actor on an entry, because commands do not carry one yet. "Who
/// released this lot" is a question the trail cannot currently answer, and it is exactly the question
/// an audit asks. Identity reaches the backend with electronic signatures, and the entry gains a
/// field then.
/// </para>
/// </remarks>
public interface ICommandAuditSink
{
    /// <summary>Records one attempt.</summary>
    /// <param name="entry">What happened.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    Task WriteAsync(CommandAuditEntry entry, CancellationToken cancellationToken);
}
