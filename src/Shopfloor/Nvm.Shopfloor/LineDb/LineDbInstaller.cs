using Npgsql;

namespace Nvm.Shopfloor.LineDb;

/// <summary>Trạng thái của dòng tin nằm ở đâu. Hệ thật có cả hai theo phiên bản script (scope §7), nên v2 dựng cả hai.</summary>
public enum LineStateStorage
{
    /// <summary>Cột theo môi trường trên chính bảng tin (<c>status_prod</c>…).</summary>
    Columns = 1,

    /// <summary>Bảng lịch riêng <c>relay.message_schedule</c>.</summary>
    Table = 2,
}

public sealed record LineDbOptions
{
    public LineStateStorage Storage { get; init; } = LineStateStorage.Table;

    /// <summary>Môi trường MOM của lần cài: <c>qa</c>, <c>stg</c> hoặc <c>prod</c>. Chỉ dùng ở biến thể cột.</summary>
    public string Environment { get; init; } = "qa";

    /// <summary>Mật khẩu của role MOM <c>nvm_mom</c>. Rỗng: không tạo role (test chạy bằng owner).</summary>
    public string? MomPassword { get; init; }
}

/// <summary>Cài schema LineDB từ các script nhúng trong assembly, theo thứ tự tên file.</summary>
public static class LineDbInstaller
{
    private static readonly string[] Environments = ["qa", "stg", "prod"];

    /// <summary>Cài lên database trống. Đã có bảng tin thì không làm gì, trừ khi <paramref name="reset"/> (xóa hết dữ liệu).</summary>
    public static void Install(string connectionString, LineDbOptions options, bool reset = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Environments.Contains(options.Environment))
        {
            throw new ArgumentException($"Environment must be one of {string.Join(", ", Environments)}.", nameof(options));
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        if (reset)
        {
            Execute(connection, "DROP SCHEMA IF EXISTS elec, cell, form, mod, relay CASCADE;");
        }
        else if (Scalar(connection, "SELECT to_regclass('relay.line_message') IS NOT NULL;") is true)
        {
            return;
        }

        var skipped = options.Storage == LineStateStorage.Table ? "03-state-columns.sql" : "03-state-table.sql";
        using var transaction = connection.BeginTransaction();
        foreach (var script in Scripts().Where(s => !s.Name.EndsWith(skipped, StringComparison.Ordinal)))
        {
            if (script.Name.EndsWith("06-roles.sql", StringComparison.Ordinal) && string.IsNullOrEmpty(options.MomPassword))
            {
                continue;
            }

            var sql = script.Text
                .Replace("{env}", options.Environment, StringComparison.Ordinal)
                .Replace("{mom_password}", options.MomPassword?.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal);
            Execute(connection, sql, transaction);
        }

        transaction.Commit();
    }

    private static IEnumerable<(string Name, string Text)> Scripts()
    {
        var assembly = typeof(LineDbInstaller).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (name, reader.ReadToEnd());
        }
    }

    // Script là file nhúng của chính assembly, không có dữ liệu người dùng.
#pragma warning disable CA2100
    private static void Execute(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction = null)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteScalar();
    }
#pragma warning restore CA2100

    /// <summary>Tên script đã nhúng, theo thứ tự cài. Dùng để kiểm gói.</summary>
    public static IReadOnlyList<string> ScriptNames => [.. Scripts().Select(s => s.Name)];
}
