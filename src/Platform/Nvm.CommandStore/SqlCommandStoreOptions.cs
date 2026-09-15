namespace Nvm.CommandStore;

/// <summary>Cấu hình command database; credential lấy từ môi trường.</summary>
public sealed class SqlCommandStoreOptions
{
    /// <summary>Connection dành cho runtime, không dùng credential migration.</summary>
    public string ConnectionString { get; set; } = "";
    /// <summary>Giới hạn chờ SQL, gồm tranh chấp claim.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;
    /// <summary>Chỉ Development được chạy command cũ có effect in-memory.</summary>
    public bool AllowVolatileCommands { get; set; }
}
