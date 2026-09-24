namespace Nvm.EventStore;

/// <summary>Runtime connection string; migration uses a separate privileged connection.</summary>
public sealed class SqlEventStoreOptions
{
    public required string ConnectionString { get; init; }
    public int CommandTimeoutSeconds { get; init; } = 30;
    public string SourceApplicationName { get; init; } = "app-execution";
}
