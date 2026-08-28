namespace Nvm.Ingestion.FileDrop;

/// <summary>Where dropped files arrive and where they go afterwards.</summary>
public sealed class FileDropOptions
{
    /// <summary>Directory an old machine writes its exports into.</summary>
    public string InboxPath { get; set; } = "/var/lib/nvm-ingestion/inbox";

    /// <summary>Where a file goes once every line of it has been dealt with.</summary>
    public string ProcessedPath { get; set; } = "/var/lib/nvm-ingestion/processed";

    /// <summary>Where refused lines and unreadable files go, each with a <c>.error</c> beside it.</summary>
    public string RejectedPath { get; set; } = "/var/lib/nvm-ingestion/rejected";

    /// <summary>How often the inbox is looked at.</summary>
    /// <remarks>
    /// Polling rather than a filesystem watcher. The inbox is usually an SMB or NFS share, and change
    /// notifications on those are unreliable in exactly the way that matters: they are missed
    /// silently, so a file sits there and nobody learns it was not processed until a report is short.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a file must be unmodified before it is read.</summary>
    /// <remarks>
    /// A tester writing a 40 MB export over a share leaves a partial file visible for seconds. Reading
    /// it then would ingest half a run and move it to processed, and the second half would never be
    /// seen again — a loss with no error anywhere.
    /// </remarks>
    public TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Whether the file-drop adapter runs at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>Refuses a configuration that could not process a file.</summary>
    /// <exception cref="InvalidOperationException">A path is missing or an interval is not positive.</exception>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(InboxPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProcessedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(RejectedPath);

        if (PollInterval <= TimeSpan.Zero || SettleTime < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The poll interval must be positive and the settle time cannot be negative.");
        }

        if (string.Equals(InboxPath, ProcessedPath, StringComparison.Ordinal)
            || string.Equals(InboxPath, RejectedPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The inbox must differ from the processed and rejected directories, or a file moved "
                + "out of it would be picked up again on the next poll and stored a second time — "
                + "which deduplication would swallow, leaving a loop nobody can see.");
        }
    }
}
