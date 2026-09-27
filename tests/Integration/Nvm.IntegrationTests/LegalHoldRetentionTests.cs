using Npgsql;
using Nvm.Ingestion.Persistence;

namespace Nvm.IntegrationTests;

/// <summary>
/// M12 (scope §8.4): legal hold chặn MỌI retention; retention 400 ngày (raw) và 15 năm (rollup) chỉ quay lại sau hold.
/// Bản ghi mới nhận nhưng đồng hồ máy chỉ về quá khứ (ADR-011) cũng không bị xoá.
/// </summary>
public sealed class LegalHoldRetentionTests
{
    private const string Raw = """{"relation": "ts.telemetry_measurement", "drop_after": "400 days", "check_recorded_at": true}""";
    private const string Rollup = """{"relation": "ts.process_signal_1m", "drop_after": "15 years"}""";

    [Fact]
    public async Task RawRetention_DropsOnlyExpiredChunks_ThatNoHoldCovers_AndThatHoldNoFreshRecord()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());
        await using var data = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await TelemetryHypertableTests.PauseBackgroundJobsAsync(data);

        (await TelemetryHypertableTests.ReadAsync(data, """
            SELECT count(*)::text FROM timescaledb_information.jobs WHERE proc_schema = 'ts' AND proc_name = 'enforce_retention';
            """)).ShouldBe(["3"]);
        (await TelemetryHypertableTests.ReadAsync(data, """
            SELECT count(*)::text FROM timescaledb_information.jobs WHERE proc_name = 'policy_retention';
            """)).ShouldBe(["0"]);   // không còn retention nào không hỏi hold

        await InsertAsync(data, "expired", deviceDaysAgo: 500, recordedDaysAgo: 500);
        await InsertAsync(data, "held", deviceDaysAgo: 450, recordedDaysAgo: 450);
        await InsertAsync(data, "wrong-clock", deviceDaysAgo: 600, recordedDaysAgo: 0);
        await InsertAsync(data, "recent", deviceDaysAgo: 10, recordedDaysAgo: 10);
        await TelemetryHypertableTests.ExecuteAsync(data, """
            INSERT INTO ts.legal_hold (hold_id, site_id, from_ts, to_ts, reason, placed_by)
            VALUES ('LH-RECALL-1', 'NV1', now() - INTERVAL '455 days', now() - INTERVAL '445 days',
                    'Điều tra recall lô ELY-SUP-240612-A778', 'compliance.officer');
            """);

        await TelemetryHypertableTests.ExecuteAsync(data, $"CALL ts.enforce_retention(0, '{Raw}');");
        (await Remaining(data)).ShouldBe("held,recent,wrong-clock");
        (await TelemetryHypertableTests.ReadAsync(data, """
            SELECT string_agg(action || coalesce(':' || array_to_string(hold_ids, ','), ''), ',' ORDER BY range_start)
            FROM ts.retention_log;
            """)).ShouldBe(["recently_recorded,dropped,held:LH-RECALL-1"]);

        // Hold không xoá được, không sửa được; chỉ được thả, một lần.
        await Should.ThrowAsync<PostgresException>(() => TelemetryHypertableTests.ExecuteAsync(data,
            "DELETE FROM ts.legal_hold WHERE hold_id = 'LH-RECALL-1';"));
        await Should.ThrowAsync<PostgresException>(() => TelemetryHypertableTests.ExecuteAsync(data,
            "UPDATE ts.legal_hold SET to_ts = to_ts - INTERVAL '9 days' WHERE hold_id = 'LH-RECALL-1';"));
        await TelemetryHypertableTests.ExecuteAsync(data, """
            UPDATE ts.legal_hold SET released_by = 'compliance.officer', released_at = now() WHERE hold_id = 'LH-RECALL-1';
            """);
        await Should.ThrowAsync<PostgresException>(() => TelemetryHypertableTests.ExecuteAsync(data,
            "UPDATE ts.legal_hold SET released_by = 'someone.else' WHERE hold_id = 'LH-RECALL-1';"));

        await TelemetryHypertableTests.ExecuteAsync(data, $"CALL ts.enforce_retention(0, '{Raw}');");
        (await Remaining(data)).ShouldBe("recent,wrong-clock");
    }

    [Fact]
    public async Task RollupRetention_IsBlockedByTheSameHold()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());
        await using var data = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await TelemetryHypertableTests.PauseBackgroundJobsAsync(data);
        await InsertAsync(data, "sixteen-years", deviceDaysAgo: 16 * 366, recordedDaysAgo: 16 * 366);
        await TelemetryHypertableTests.ExecuteAsync(data, """
            CALL refresh_continuous_aggregate('ts.process_signal_1m', now() - INTERVAL '5900 days', now() - INTERVAL '5800 days');
            """);
        (await RollupRows(data)).ShouldBe("1");
        await TelemetryHypertableTests.ExecuteAsync(data, """
            INSERT INTO ts.legal_hold (hold_id, site_id, from_ts, to_ts, reason, placed_by)
            VALUES ('LH-AUDIT-2010S', 'NV1', now() - INTERVAL '6000 days', now() - INTERVAL '5000 days', 'Kiểm toán', 'reg.liaison');
            """);

        await TelemetryHypertableTests.ExecuteAsync(data, $"CALL ts.enforce_retention(0, '{Rollup}');");
        (await RollupRows(data)).ShouldBe("1");
        await TelemetryHypertableTests.ExecuteAsync(data, """
            UPDATE ts.legal_hold SET released_by = 'reg.liaison', released_at = now() WHERE hold_id = 'LH-AUDIT-2010S';
            """);
        await TelemetryHypertableTests.ExecuteAsync(data, $"CALL ts.enforce_retention(0, '{Rollup}');");
        (await RollupRows(data)).ShouldBe("0");
        (await TelemetryHypertableTests.ReadAsync(data, "SELECT string_agg(action, ',' ORDER BY log_id) FROM ts.retention_log;"))
            .ShouldBe(["held,dropped"]);
    }

    private static async Task InsertAsync(NpgsqlDataSource data, string key, int deviceDaysAgo, int recordedDaysAgo)
    {
        await using var command = data.CreateCommand("""
            WITH claim AS (
                INSERT INTO ingest.processed_message (source_event_id, site_id, natural_key, first_seen_at)
                VALUES (gen_random_uuid(), 'NV1', @key, now() - make_interval(days => @recorded))
                RETURNING source_event_id)
            INSERT INTO ts.telemetry_measurement (source_event_id, site_id, equipment_id, unit_id, step_code, signal_code,
                device_timestamp, gateway_timestamp, recorded_at, value_kind, real_value, clock_quality)
            SELECT source_event_id, 'NV1', 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001', @key, 'FORMATION', 'cell_voltage',
                now() - make_interval(days => @device), now() - make_interval(days => @recorded),
                now() - make_interval(days => @recorded), 'real', 3.6, 'Good'
            FROM claim;
            """);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("device", deviceDaysAgo);
        command.Parameters.AddWithValue("recorded", recordedDaysAgo);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> Remaining(NpgsqlDataSource data) =>
        (await TelemetryHypertableTests.ReadAsync(data,
            "SELECT string_agg(unit_id, ',' ORDER BY unit_id) FROM ts.telemetry_measurement;"))[0];

    private static async Task<string> RollupRows(NpgsqlDataSource data) =>
        (await TelemetryHypertableTests.ReadAsync(data, "SELECT count(*)::text FROM ts.process_signal_1m;"))[0];
}
