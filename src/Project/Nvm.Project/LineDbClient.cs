using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Nvm.Project;

/// <summary>
/// Phần code của team nói chuyện với LineDB: chỉ gọi hai hàm của schema trung gian (lấy dòng, ghi kết quả), bằng role không
/// đọc được bảng máy (K11).
/// </summary>
internal static class LineDbClient
{
    /// <summary>Lấy tối đa <paramref name="limit"/> dòng của một mã bước (mới, hoặc tới lượt gửi lại), đánh dấu SENT.</summary>
    /// <returns>JSON <c>{"rows":[…]}</c> cùng dạng với payload của relay, và số dòng.</returns>
    public static (string Payload, int Rows) Claim(string connectionString, string operationCode, int limit, DateTimeOffset now)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json_build_object('rows', COALESCE(json_agg(c ORDER BY c.sequence_id), '[]'::json))::text, count(*)::int
            FROM relay.claim(@operation, @limit, 'poll', @now) c;
            """;
        command.Parameters.AddWithValue("operation", operationCode);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetString(0), reader.GetInt32(1));
    }

    /// <summary>Ghi kết quả từng dòng trong một transaction. LineDB tự chuyển RETRY sang FAILED khi hết lượt theo chính sách.</summary>
    public static void WriteResults(string connectionString, IReadOnlyList<LineRowResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var result in results)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT relay.write_result(@id, @status, @code);";
            command.Parameters.AddWithValue("id", result.SequenceId);
            command.Parameters.AddWithValue("status", result.Status);
            command.Parameters.AddWithValue("code", result.ErrorCode);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}

/// <summary>Một dòng tin đã tách: tin sản phẩm thành <see cref="Detail"/>, tin hao hụt thành <see cref="Shrink"/>; dòng hỏng kèm lý do.</summary>
internal sealed record ParsedLineRow(long SequenceId, string Kind, LineOperationDetail? Detail, LineShrinkDetail? Shrink, string? Error);

/// <summary>
/// Tách payload của LineDB. Trigger mỗi bảng máy ghi một trong ba dạng: <c>unit</c> (một sản phẩm, nguyên liệu kèm lô),
/// <c>batch</c> (một lô có lượng, lô nguyên liệu kèm lượng), <c>shrink</c> (hao hụt của một lô nguyên liệu).
/// </summary>
internal static class LineMessageParser
{
    public static IReadOnlyList<ParsedLineRow> Parse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var rows = new List<ParsedLineRow>();
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var sequenceId = row.GetProperty("sequence_id").GetInt64();
            var body = row.GetProperty("payload");
            var kind = Text(body, "kind") ?? "?";
            rows.Add(kind switch
            {
                "unit" => new ParsedLineRow(sequenceId, kind, Detail(row, body, Text(body, "serial"), Number(body, "qty"), "materials"), null, null),
                "batch" => new ParsedLineRow(sequenceId, kind, Detail(row, body, Text(body, "batch_id"), Number(body, "length"), "lots"), null, null),
                "shrink" => new ParsedLineRow(sequenceId, kind, null, Shrink(row, body), null),
                _ => new ParsedLineRow(sequenceId, kind, null, null, $"Unknown message kind '{kind}'."),
            });
        }

        return rows;
    }

    private static LineOperationDetail Detail(JsonElement row, JsonElement body, string? serial, decimal? quantity, string materialsProperty) => new()
    {
        SequenceId = row.GetProperty("sequence_id").GetInt64(),
        OperationCode = Text(row, "operation_code"),
        EquipmentCode = Text(row, "equipment_code"),
        Serial = serial ?? Text(row, "serial"),
        Quantity = quantity,
        StartTime = Time(body, "start"),
        EndTime = Time(body, "end"),
        StatusId = Text(body, "result"),
        StatusCode = Text(body, "code"),
        RunMode = RunModes.FromClass(body.TryGetProperty("class", out var runClass) && runClass.ValueKind == JsonValueKind.Number ? runClass.GetInt32() : null),
        Materials = body.TryGetProperty(materialsProperty, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(m => new LineMaterialUse { MaterialCode = Text(m, "item"), Lot = Text(m, "lot"), Quantity = Number(m, "qty") })]
            : [],
        ContainerCode = Text(body, "container"),
        Contents = body.TryGetProperty("contents", out var contents) && contents.ValueKind == JsonValueKind.Array
            ? [.. contents.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!)]
            : null,
    };

    private static LineShrinkDetail Shrink(JsonElement row, JsonElement body) => new()
    {
        SequenceId = row.GetProperty("sequence_id").GetInt64(),
        OperationCode = Text(row, "operation_code"),
        EquipmentCode = Text(row, "equipment_code"),
        MaterialCode = Text(body, "item"),
        Lot = Text(body, "lot"),
        Quantity = Number(body, "qty"),
        UnitOfMeasure = Text(body, "unit"),
        StatusCode = Text(body, "code"),
        EventTime = Time(body, "at"),
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;
}
