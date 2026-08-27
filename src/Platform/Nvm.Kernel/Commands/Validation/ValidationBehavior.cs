namespace Nvm.Kernel.Commands.Validation;

/// <summary>Stops a malformed command at the door, before anything else in the pipeline runs.</summary>
/// <typeparam name="TCommand">The command being handled.</typeparam>
/// <typeparam name="TResult">What handling it yields.</typeparam>
/// <param name="validators">
/// Every validator registered for this command. Usually none or one; more than one is allowed so a
/// Functional Block can add a rule to a command it does not own.
/// </param>
/// <remarks>
/// <para>
/// Outermost, so a malformed command is refused using nothing but itself — no clock, no store, no
/// round trip. Cheap first is the ordinary reason; the sharper one arrives with the real
/// deduplication store, which will have to claim a key on the way in to stop two simultaneous copies
/// of a command from both running. Once it claims, anything that reaches it has marked its key as
/// taken, and the corrected resend of a rejected command — same natural key, same idempotency key —
/// is swallowed as a duplicate.
/// </para>
/// <para>
/// That case is real rather than theoretical: the key comes from the natural key, so a mistake in any
/// field outside it produces a different command carrying the same key. See
/// <see cref="KernelServiceCollectionExtensions.AddNvmKernel"/> for the full ordering argument.
/// </para>
/// </remarks>
public sealed class ValidationBehavior<TCommand, TResult>(IEnumerable<ICommandValidator<TCommand>> validators)
    : ICommandBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    private readonly IEnumerable<ICommandValidator<TCommand>> _validators = validators;

    /// <inheritdoc />
    public Task<TResult> HandleAsync(
        TCommand command,
        CommandPipelineStep<TResult> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(continuation);

        // Every validator runs, and every failure is collected. Stopping at the first one turns fixing
        // a form into a guessing game played one round at a time.
        var failures = _validators
            .SelectMany(validator => validator.Validate(command))
            .ToList();

        return failures.Count == 0
            ? continuation()
            : throw new CommandValidationException(typeof(TCommand), failures);
    }
}
