namespace Nvm.Kernel.Commands;

/// <summary>
/// A request to change something, addressed to exactly one handler.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of a domain event. An event is a fact that already happened and is stated in the
/// past tense; a command is an intention that may still be refused, and is named in the imperative:
/// <c>ActivateFactoryModelRevision</c>, <c>QuarantineUnit</c>, <c>ApproveRecipeVersion</c>.
/// </para>
/// <para>
/// This non-generic interface exists so that the pipeline can read
/// <see cref="IdempotencyKey"/> off a command without knowing what the command returns.
/// Implementations use <see cref="ICommand{TResult}"/>.
/// </para>
/// </remarks>
public interface ICommand
{
    /// <summary>
    /// Identifies the intention itself, so that receiving it twice does not perform it twice.
    /// </summary>
    /// <remarks>
    /// Required rather than optional (AGENTS.md K7). An optional key would be omitted exactly where
    /// it matters most — in the retry path, written by whoever was in a hurry.
    /// </remarks>
    IdempotencyKey IdempotencyKey { get; }
}

/// <summary>A command whose handler produces <typeparamref name="TResult"/>.</summary>
/// <typeparam name="TResult">What handling the command yields, usually the event it caused.</typeparam>
/// <remarks>
/// <para>
/// There is deliberately no void-returning variant. In this domain a command that changes anything
/// produces at least the event that records the change, and the caller needs that event — to publish
/// it, to return it, or to write it to the store. A handler that has nothing to give back is usually
/// a handler that forgot to record what it did.
/// </para>
/// <para>
/// Keeping one shape also keeps one dispatch path. Two would double the generic machinery in
/// <see cref="ICommandDispatcher"/> for no benefit anybody has asked for yet.
/// </para>
/// </remarks>
public interface ICommand<out TResult> : ICommand;
