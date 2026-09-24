namespace Nvm.Kernel.EventSourcing;

/// <summary>A source event identity already names a different persisted fact.</summary>
public sealed class EventIdentityConflictException(Guid sourceEventId)
    : Exception($"Source event identity {sourceEventId} already belongs to another fact.")
{
    public Guid SourceEventId { get; } = sourceEventId;
}
