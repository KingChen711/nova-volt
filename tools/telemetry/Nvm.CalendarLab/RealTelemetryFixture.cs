using Npgsql;
using NpgsqlTypes;

namespace Nvm.CalendarLab;

internal sealed record RealTelemetryFixture(
    IReadOnlyList<CalendarAssignment> Assignments,
    int ChannelCount,
    long GoodRows,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt);

internal static class RealTelemetryFixtureReader
{
    private const string SiteId = "NV1";
    private const string SignalCode = "Formation/Temperature";
    private const long ExpectedRows = 121_429;
    private const int ExpectedChannels = 100;

    private static readonly DateTimeOffset StartAt =
        new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset EndAt =
        new(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ExpectedLastAt =
        new(2026, 7, 26, 23, 59, 50, TimeSpan.Zero);

    private const string FixtureSql =
        """
        SELECT measurements.device_timestamp,
               CAST(measurements.device_timestamp AS date) AS naive_date,
               measurements.equipment_id,
               measurements.clock_quality
        FROM ts.telemetry_measurement AS measurements
        WHERE measurements.site_id = @site_id
          AND measurements.device_timestamp >= @start_at
          AND measurements.device_timestamp < @end_at
          AND measurements.signal_code = @signal_code
          AND measurements.value_kind = 'real';
        """;

    internal static async Task<RealTelemetryFixture> ReadAsync(
        string connectionString,
        TimeZoneInfo siteZone,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(siteZone);

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (var setZone = new NpgsqlCommand(
            "SELECT set_config('TimeZone', @zone_id, false);",
            connection))
        {
            setZone.Parameters.AddWithValue("zone_id", NpgsqlDbType.Text, siteZone.Id);
            _ = await setZone.ExecuteScalarAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(FixtureSql, connection)
        {
            CommandTimeout = 120,
        };
        command.Parameters.AddWithValue("site_id", NpgsqlDbType.Text, SiteId);
        command.Parameters.AddWithValue("start_at", NpgsqlDbType.TimestampTz, StartAt);
        command.Parameters.AddWithValue("end_at", NpgsqlDbType.TimestampTz, EndAt);
        command.Parameters.AddWithValue("signal_code", NpgsqlDbType.Text, SignalCode);

        var assignments = new List<CalendarAssignment>((int)ExpectedRows);
        var channels = new HashSet<string>(StringComparer.Ordinal);
        long goodRows = 0;
        DateTimeOffset? firstAt = null;
        DateTimeOffset? lastAt = null;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var instant = await reader.GetFieldValueAsync<DateTimeOffset>(0, cancellationToken);
            var sqlCast = await reader.GetFieldValueAsync<DateOnly>(1, cancellationToken);
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, siteZone).DateTime);

            if (sqlCast != localDate)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL CAST produced {sqlCast} but site-local date is {localDate} at {instant:O}.");
            }

            assignments.Add(new CalendarAssignment(instant, sqlCast));
            _ = channels.Add(reader.GetString(2));

            if (string.Equals(reader.GetString(3), "Good", StringComparison.Ordinal))
            {
                goodRows++;
            }

            firstAt = firstAt is null || instant < firstAt ? instant : firstAt;
            lastAt = lastAt is null || instant > lastAt ? instant : lastAt;
        }

        var fixture = new RealTelemetryFixture(
            assignments,
            channels.Count,
            goodRows,
            firstAt ?? default,
            lastAt ?? default);

        RequireFingerprint(fixture);

        return fixture;
    }

    private static void RequireFingerprint(RealTelemetryFixture fixture)
    {
        if (fixture.Assignments.Count != ExpectedRows
            || fixture.ChannelCount != ExpectedChannels
            || fixture.GoodRows != ExpectedRows
            || fixture.FirstAt != StartAt
            || fixture.LastAt != ExpectedLastAt)
        {
            throw new InvalidOperationException(
                $"C10 fixture fingerprint mismatch: expected rows/channels/good/range "
                + $"{ExpectedRows}/{ExpectedChannels}/{ExpectedRows}/[{StartAt:O}, {ExpectedLastAt:O}], found "
                + $"{fixture.Assignments.Count}/{fixture.ChannelCount}/{fixture.GoodRows}/"
                + $"[{fixture.FirstAt:O}, {fixture.LastAt:O}]. Run the fixture command printed by "
                + "make rollup-bench.");
        }
    }
}
