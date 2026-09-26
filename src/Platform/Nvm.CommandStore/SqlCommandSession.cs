using System.Data;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;

namespace Nvm.CommandStore;

/// <summary>Connection và transaction scoped dùng chung giữa claim, handler và outcome.</summary>
public sealed class SqlCommandSession : IAsyncDisposable
{
    private SqlConnection? _connection;
    private SqlTransaction? _transaction;

    /// <summary>Site của command đang giữ transaction, đã được server xác thực.</summary>
    public string SiteId { get; private set; } = "";

    /// <summary>Cho phép reader tham gia transaction hiện tại thay vì tự mở connection khác.</summary>
    public bool HasActiveTransaction => _transaction is not null;

    /// <summary>Chỉ có transaction khi caller đang giữ claim mới.</summary>
    internal SqlTransaction Transaction => _transaction ?? throw new InvalidOperationException("No active command transaction.");

    /// <summary>Tạo lệnh luôn tham gia transaction đang giữ claim.</summary>
    /// <remarks>
    /// <paramref name="sql"/> luôn là chuỗi hằng do chính infrastructure viết ra; mọi giá trị từ người
    /// dùng đi qua <see cref="SqlParameter"/>. Không có đường nào để input ghép vào câu lệnh.
    /// </remarks>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "SQL là chuỗi hằng của infrastructure; mọi giá trị người dùng đều tham số hoá.")]
    public SqlCommand CreateCommand(string sql)
        => new(sql, _connection ?? throw new InvalidOperationException("No active command connection."), Transaction);

    /// <summary>Bulk copy trong cùng transaction (ví dụ nạp bảng tạm cho thao tác set-based).</summary>
    public SqlBulkCopy CreateBulkCopy(string destinationTable) =>
        new(_connection ?? throw new InvalidOperationException("No active command connection."), SqlBulkCopyOptions.Default,
            Transaction)
        { DestinationTableName = destinationTable, BulkCopyTimeout = 300 };

    internal async Task BeginAsync(string connectionString, string siteId, CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            throw new InvalidOperationException("Concurrent or nested commands require separate scopes.");
        }

        _connection = new SqlConnection(connectionString);
        SiteId = siteId;
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        _transaction = (SqlTransaction)await _connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
    }

    internal async Task CommitAsync(CancellationToken cancellationToken)
    {
        await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Dispose transaction chưa commit sẽ rollback; không DELETE claim đã commit khi mất phản hồi.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_transaction is not null)
            {
                await _transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _transaction = null;
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
    }
}
