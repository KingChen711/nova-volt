using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Nvm.Kernel.EventSourcing;

/// <summary>A site-scoped stream and its ordered events.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "An event stream is a domain term, not System.IO.Stream.")]
public sealed record EventStream(
    string SiteId,
    string StreamId,
    string StreamType,
    long Version,
    ImmutableArray<StoredStreamEvent> Events);
