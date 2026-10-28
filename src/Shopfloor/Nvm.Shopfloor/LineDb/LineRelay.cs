using Npgsql;

namespace Nvm.Shopfloor.LineDb;

/// <summary>
/// Đường relay: phía xưởng có lịch chạy mỗi phút gọi <c>relay.send_pending</c> (gom tin theo mã bước rồi notify); bên nghe
/// nhận notify và đẩy payload sang MOM thành event. Bên nghe không chạy thì notify mất, dòng vẫn ở SENT.
/// </summary>
public static class LineRelay
{
    public const string Channel = "line_messages";

    /// <summary>Một nhịp của lịch phía xưởng. Trả số dòng đã notify thành công.</summary>
    public static int SendPending(string connectionString, int rowsPerMessage, DateTimeOffset now)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT relay.send_pending(@rows, @now);", connection);
        command.Parameters.AddWithValue("rows", rowsPerMessage);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        return (int)command.ExecuteScalar()!;
    }
}

/// <summary>Bên nghe notify của LineDB (vai bộ tích hợp của nhà cung cấp nền): mỗi notify là một lần gọi <c>onMessage</c>.</summary>
public sealed class LineNotifyListener(string connectionString, Func<string, Task> onMessage) : IAsyncDisposable
{
    private NpgsqlConnection? _connection;

    /// <summary>Đã LISTEN xong: notify gửi từ lúc này trở đi sẽ tới.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _connection = new NpgsqlConnection(connectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        _connection.Notification += (_, e) =>
        {
            if (e.Channel == LineRelay.Channel)
            {
                onMessage(e.Payload).GetAwaiter().GetResult();
            }
        };
        await using var listen = new NpgsqlCommand($"LISTEN {LineRelay.Channel};", _connection);
        await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Chờ và xử lý notify cho tới khi hủy.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await _connection!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Xử lý các notify đã tới, chờ tối đa <paramref name="timeout"/> cho cái đầu tiên. Trả true nếu có notify.</summary>
    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("Call StartAsync first.");
        }

        var any = false;
        while (await _connection.WaitAsync(any ? TimeSpan.FromMilliseconds(200) : timeout, cancellationToken).ConfigureAwait(false))
        {
            any = true;
        }

        return any;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
