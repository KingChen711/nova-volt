using Npgsql;

namespace Nvm.Shopfloor;

/// <summary>
/// Nhịp danh định của một line: cứ <see cref="Takt"/> máy lắp ra một cell; cell vào formation sau <see cref="FormationDelay"/>;
/// đủ cell đã formation thì ghép module; cuộn điện cực mới khi cuộn đang dùng không đủ cho một cell (1,2 m).
/// </summary>
public sealed record LineLoadProfile
{
    /// <summary>Danh định: 10 giây một cell mỗi line. Tải gấp đôi là 5 giây.</summary>
    public static readonly TimeSpan NominalTakt = TimeSpan.FromSeconds(10);

    public IReadOnlyList<int> Lines { get; init; } = [1, 2, 3, 4];

    public TimeSpan Takt { get; init; } = NominalTakt;

    public TimeSpan FormationDelay { get; init; } = TimeSpan.FromMinutes(2);

    public decimal CoilLength { get; init; } = 120m;

    public string Prefix { get; init; } = "LD";
}

/// <summary>
/// Sinh tin theo giờ thật: mỗi lần <see cref="Tick"/> ghi những dòng máy đã tới giờ. Dùng cho phép đo tải (độ trễ từ lúc dòng
/// vào bảng trung gian tới lúc MOM ghi kết quả).
/// </summary>
public sealed class LineLoadGenerator(string connectionString, LineLoadProfile profile, DateTimeOffset start)
{
    private readonly Dictionary<int, LineState> _lines = profile.Lines.ToDictionary(l => l, _ => new LineState { NextCell = start });

    public int RowsWritten { get; private set; }

    /// <summary>Ghi mọi dòng tới hạn tại <paramref name="now"/>. Trả số dòng đã ghi trong nhịp này.</summary>
    public int Tick(DateTimeOffset now)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var rows = new LineRows(connection, transaction);
        var written = 0;
        foreach (var (line, state) in _lines)
        {
            var family = LineSimulator.Family(line);
            var id = $"{profile.Prefix}L{line}";
            while (state.NextCell <= now)
            {
                if (state.CoilLeft < 1.2m)
                {
                    state.Coils++;
                    state.Coil = $"{id}-EC{state.Coils:00000}";
                    state.CoilLeft = profile.CoilLength;
                    rows.Coil(line, state.Coil, profile.CoilLength, $"AN-{state.Coil}", $"CA-{state.Coil}", null, state.NextCell);
                    written++;
                }

                state.Cells++;
                var cell = $"{id}-C{state.Cells:000000}";
                rows.Cell(line, family, cell, state.Coil!, ko: false, componentLots: null, state.NextCell);
                state.CoilLeft -= 1.2m;
                state.Forming.Enqueue((cell, state.NextCell + profile.FormationDelay));
                state.NextCell += profile.Takt;
                written++;
            }

            while (state.Forming.TryPeek(out var next) && next.Due <= now)
            {
                state.Forming.Dequeue();
                rows.Formation(line, family, next.Cell, next.Due);
                state.Formed.Add(next.Cell);
                written++;
                if (state.Formed.Count == LineSimulator.CellsPerModule(line))
                {
                    state.Modules++;
                    rows.Module(line, family, $"{id}-M{state.Modules:00000}", [.. state.Formed], next.Due);
                    state.Formed.Clear();
                    written++;
                }
            }
        }

        transaction.Commit();
        RowsWritten += written;
        return written;
    }

    private sealed class LineState
    {
        public DateTimeOffset NextCell { get; set; }

        public string? Coil { get; set; }

        public decimal CoilLeft { get; set; }

        public int Coils { get; set; }

        public int Cells { get; set; }

        public int Modules { get; set; }

        public Queue<(string Cell, DateTimeOffset Due)> Forming { get; } = new();

        public List<string> Formed { get; } = [];
    }
}

/// <summary>Số đo của bảng trung gian từ một mốc: dòng theo trạng thái, tồn đọng, độ trễ có kết quả.</summary>
public sealed record LineDbSnapshot(
    int Rows,
    int Done,
    int Backlog,
    int Failed,
    double? P50Seconds,
    double? P95Seconds,
    double? MaxSeconds);

public static class LineDbReport
{
    /// <summary>Đọc bằng owner. Độ trễ = <c>last_result_on − created_on</c> của dòng DONE tạo từ <paramref name="since"/>.</summary>
    public static LineDbSnapshot Snapshot(string connectionString, DateTimeOffset since)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""
            SELECT count(*)::int,
                   count(*) FILTER (WHERE s.status = 'DONE')::int,
                   count(*) FILTER (WHERE s.status IN ('NEW', 'SENT'))::int,
                   count(*) FILTER (WHERE s.status IN ('RETRY', 'FAILED', 'NOTIFY_ERR'))::int,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM s.last_result_on - m.created_on))
                       FILTER (WHERE s.status = 'DONE'),
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM s.last_result_on - m.created_on))
                       FILTER (WHERE s.status = 'DONE'),
                   max(extract(epoch FROM s.last_result_on - m.created_on)) FILTER (WHERE s.status = 'DONE')::float8
            FROM relay.line_message m JOIN relay.message_status s ON s.sequence_id = m.sequence_id
            WHERE m.created_on >= @since;
            """, connection);
        command.Parameters.AddWithValue("since", since.ToUniversalTime());
        using var reader = command.ExecuteReader();
        reader.Read();
        return new LineDbSnapshot(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6));
    }
}
