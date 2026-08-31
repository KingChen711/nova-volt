\set ON_ERROR_STOP on

SET client_min_messages = warning;
SET lock_timeout = '30s';
-- 90 minutes, not 20. The measurement loop is one statement, and the volume axis added by
-- ADR-034 put a 12,25 million row window through compress/decompress/compress inside it - about
-- 5,6 times the largest window this script used to touch. A timeout that kills the run after an
-- hour of real work is a timeout that makes the measurement impossible to take.
SET statement_timeout = '90min';

CREATE TEMP TABLE c09_compression_report (
    ordinal                         INTEGER        PRIMARY KEY,
    scenario                        TEXT           NOT NULL UNIQUE,
    site_id                         TEXT           NOT NULL,
    start_at                        TIMESTAMPTZ    NOT NULL,
    end_at                          TIMESTAMPTZ    NOT NULL,
    window_days                     INTEGER        NOT NULL,
    rows_before                     BIGINT         NOT NULL,
    rows_after                      BIGINT         NOT NULL,
    channels                        BIGINT         NOT NULL,
    cardinality                     BIGINT         NOT NULL,
    chunks                          BIGINT         NOT NULL,
    detailed_before_bytes           BIGINT         NOT NULL,
    detailed_after_bytes            BIGINT         NOT NULL,
    stats_before_bytes              BIGINT         NOT NULL,
    stats_after_bytes               BIGINT         NOT NULL,
    hypertable_before_bytes         BIGINT         NOT NULL,
    hypertable_after_bytes          BIGINT         NOT NULL,
    ratio_percent                   NUMERIC        NOT NULL
) ON COMMIT PRESERVE ROWS;

BEGIN;

-- Compression works at chunk granularity. SHARE prevents ingestion or the policy job from changing
-- any chunk while sizes are read; reads, including the rollup refresh, can continue.
LOCK TABLE ts.telemetry_measurement IN SHARE MODE;

DO $measurement$
DECLARE
    scenario RECORD;
    target RECORD;
    chunk_count BIGINT;
    row_count_before BIGINT;
    row_count_after BIGINT;
    channel_count BIGINT;
    series_cardinality BIGINT;
    unexpected_site_rows BIGINT;
    first_device_timestamp TIMESTAMPTZ;
    last_device_timestamp TIMESTAMPTZ;
    detailed_before BIGINT;
    detailed_after BIGINT;
    stats_before BIGINT;
    stats_after BIGINT;
    hypertable_before BIGINT;
    hypertable_after BIGINT;
BEGIN
    -- Twenty-five percent of clocks in every C08 fixture move by plus/minus two hours. Their
    -- boundary chunks are not full process days, so each window below is the inner span of a
    -- longer generated run and every window covers the same curve phase.
    --
    -- Four scenarios, two axes, because D1 asks two different questions (ADR-034, owner 2026-08-31):
    --
    --   cardinality axis  channels_8        vs channels_40        - same 5 days, different shape
    --   volume axis       volume_7d_at_40   vs volume_28d_at_40   - same 40 channels, 4x the rows
    --
    -- Pinning the volume axis to one cardinality is the whole point. The first two scenarios also
    -- differ 4,99x in row count, so accepting them as a volume pair would have restated the
    -- cardinality axis in different units and measured nothing new.
    FOR scenario IN
        SELECT *
        FROM (VALUES
            (1, 'channels_8'::TEXT,        'NV1'::TEXT, TIMESTAMPTZ '2026-08-16 00:00:00Z', TIMESTAMPTZ '2026-08-21 00:00:00Z',  5,  8,  437798::BIGINT),
            (2, 'channels_40'::TEXT,       'NV1'::TEXT, TIMESTAMPTZ '2026-08-02 00:00:00Z', TIMESTAMPTZ '2026-08-07 00:00:00Z',  5, 40, 2186459::BIGINT),
            (3, 'volume_7d_at_40'::TEXT,   'NV1'::TEXT, TIMESTAMPTZ '2026-05-24 00:00:00Z', TIMESTAMPTZ '2026-05-31 00:00:00Z',  7, 40, 3062323::BIGINT),
            (4, 'volume_28d_at_40'::TEXT,  'NV1'::TEXT, TIMESTAMPTZ '2026-06-03 00:00:00Z', TIMESTAMPTZ '2026-07-01 00:00:00Z', 28, 40, 12253575::BIGINT)
        ) AS configured(ordinal, scenario, site_id, start_at, end_at, window_days, expected_channels, expected_rows)
        ORDER BY ordinal
    LOOP
        SELECT count(*)
        INTO chunk_count
        FROM timescaledb_information.chunks AS chunks
        WHERE chunks.hypertable_schema = 'ts'
          AND chunks.hypertable_name = 'telemetry_measurement'
          AND chunks.range_start >= scenario.start_at
          AND chunks.range_end <= scenario.end_at;

        IF chunk_count <> scenario.window_days THEN
            RAISE EXCEPTION
                'C09 % expected % exclusive one-day chunks in [% - %), found %',
                scenario.scenario,
                scenario.window_days,
                scenario.start_at,
                scenario.end_at,
                chunk_count;
        END IF;

        -- Normalize every fixture to the compact rowstore produced by decompression. A fresh COPY
        -- can leave more free/index pages than a decompressed chunk, which made the first and second
        -- physical-size baselines differ even though their logical rows were identical.
        FOR target IN
            SELECT format('%I.%I', chunks.chunk_schema, chunks.chunk_name)::REGCLASS AS chunk,
                   chunks.is_compressed
            FROM timescaledb_information.chunks AS chunks
            WHERE chunks.hypertable_schema = 'ts'
              AND chunks.hypertable_name = 'telemetry_measurement'
              AND chunks.range_start >= scenario.start_at
              AND chunks.range_end <= scenario.end_at
            ORDER BY chunks.range_start
        LOOP
            IF NOT target.is_compressed THEN
                PERFORM compress_chunk(target.chunk, if_not_compressed => true, recompress => true);
            END IF;

            PERFORM decompress_chunk(target.chunk);
        END LOOP;

        SELECT count(*),
               count(DISTINCT measurements.equipment_id),
               count(DISTINCT (measurements.equipment_id, measurements.signal_code)),
               min(measurements.device_timestamp),
               max(measurements.device_timestamp)
        INTO row_count_before,
             channel_count,
             series_cardinality,
             first_device_timestamp,
             last_device_timestamp
        FROM ts.telemetry_measurement AS measurements
        WHERE measurements.site_id = scenario.site_id
          AND measurements.device_timestamp >= scenario.start_at
          AND measurements.device_timestamp < scenario.end_at;

        SELECT count(*)
        INTO unexpected_site_rows
        FROM ts.telemetry_measurement AS measurements
        WHERE measurements.site_id IS DISTINCT FROM scenario.site_id
          AND measurements.device_timestamp >= scenario.start_at
          AND measurements.device_timestamp < scenario.end_at;

        IF row_count_before <> scenario.expected_rows
            OR channel_count <> scenario.expected_channels
            OR series_cardinality <> scenario.expected_channels * 6
            OR unexpected_site_rows <> 0
            OR first_device_timestamp <> scenario.start_at
            OR last_device_timestamp <> scenario.end_at - INTERVAL '5 seconds' THEN
            RAISE EXCEPTION
                'C09 % fixture fingerprint mismatch: expected rows=% channels=% cardinality=% range=[%, %], found rows=% channels=% cardinality=% other_site_rows=% range=[%, %]',
                scenario.scenario,
                scenario.expected_rows,
                scenario.expected_channels,
                scenario.expected_channels * 6,
                scenario.start_at,
                scenario.end_at - INTERVAL '5 seconds',
                row_count_before,
                channel_count,
                series_cardinality,
                unexpected_site_rows,
                first_device_timestamp,
                last_device_timestamp;
        END IF;

        SELECT coalesce(sum(sizes.total_bytes), 0)::BIGINT
        INTO detailed_before
        FROM chunks_detailed_size('ts.telemetry_measurement') AS sizes
        JOIN timescaledb_information.chunks AS chunks
          ON chunks.chunk_schema = sizes.chunk_schema
         AND chunks.chunk_name = sizes.chunk_name
        WHERE chunks.hypertable_schema = 'ts'
          AND chunks.hypertable_name = 'telemetry_measurement'
          AND chunks.range_start >= scenario.start_at
          AND chunks.range_end <= scenario.end_at;

        SELECT sizes.total_bytes
        INTO STRICT hypertable_before
        FROM hypertable_detailed_size('ts.telemetry_measurement') AS sizes;

        FOR target IN
            SELECT format('%I.%I', chunks.chunk_schema, chunks.chunk_name)::REGCLASS AS chunk
            FROM timescaledb_information.chunks AS chunks
            WHERE chunks.hypertable_schema = 'ts'
              AND chunks.hypertable_name = 'telemetry_measurement'
              AND chunks.range_start >= scenario.start_at
              AND chunks.range_end <= scenario.end_at
            ORDER BY chunks.range_start
        LOOP
            PERFORM compress_chunk(target.chunk, if_not_compressed => true, recompress => true);
        END LOOP;

        SELECT count(*)
        INTO row_count_after
        FROM ts.telemetry_measurement AS measurements
        WHERE measurements.site_id = scenario.site_id
          AND measurements.device_timestamp >= scenario.start_at
          AND measurements.device_timestamp < scenario.end_at;

        SELECT coalesce(sum(sizes.total_bytes), 0)::BIGINT
        INTO detailed_after
        FROM chunks_detailed_size('ts.telemetry_measurement') AS sizes
        JOIN timescaledb_information.chunks AS chunks
          ON chunks.chunk_schema = sizes.chunk_schema
         AND chunks.chunk_name = sizes.chunk_name
        WHERE chunks.hypertable_schema = 'ts'
          AND chunks.hypertable_name = 'telemetry_measurement'
          AND chunks.range_start >= scenario.start_at
          AND chunks.range_end <= scenario.end_at;

        SELECT coalesce(sum(stats.before_compression_total_bytes), 0)::BIGINT,
               coalesce(sum(stats.after_compression_total_bytes), 0)::BIGINT
        INTO stats_before, stats_after
        FROM chunk_compression_stats('ts.telemetry_measurement') AS stats
        JOIN timescaledb_information.chunks AS chunks
          ON chunks.chunk_schema = stats.chunk_schema
         AND chunks.chunk_name = stats.chunk_name
        WHERE chunks.hypertable_schema = 'ts'
          AND chunks.hypertable_name = 'telemetry_measurement'
          AND chunks.range_start >= scenario.start_at
          AND chunks.range_end <= scenario.end_at;

        SELECT sizes.total_bytes
        INTO STRICT hypertable_after
        FROM hypertable_detailed_size('ts.telemetry_measurement') AS sizes;

        IF row_count_after <> row_count_before THEN
            RAISE EXCEPTION
                'C09 % compression changed row count from % to %',
                scenario.scenario,
                row_count_before,
                row_count_after;
        END IF;

        IF detailed_before <= 0 OR detailed_after <= 0 OR stats_before <= 0 OR stats_after <= 0 THEN
            RAISE EXCEPTION
                'C09 % produced non-positive bytes: detailed=%/% stats=%/%',
                scenario.scenario,
                detailed_before,
                detailed_after,
                stats_before,
                stats_after;
        END IF;

        -- The live detailed size includes the residual rowstore relation that remains beside a
        -- compressed chunk. Compression stats expose only the compressed relation after the write,
        -- so the live after-size is allowed to be larger but never smaller.
        IF detailed_before <> stats_before OR detailed_after < stats_after THEN
            RAISE EXCEPTION
                'C09 % size oracles disagree: detailed=%/% compression_stats=%/%',
                scenario.scenario,
                detailed_before,
                detailed_after,
                stats_before,
                stats_after;
        END IF;

        IF hypertable_before - hypertable_after <> detailed_before - detailed_after THEN
            RAISE EXCEPTION
                'C09 % hypertable delta % does not equal selected-chunk delta %',
                scenario.scenario,
                hypertable_before - hypertable_after,
                detailed_before - detailed_after;
        END IF;

        INSERT INTO c09_compression_report (
            ordinal,
            scenario,
            site_id,
            start_at,
            end_at,
            window_days,
            rows_before,
            rows_after,
            channels,
            cardinality,
            chunks,
            detailed_before_bytes,
            detailed_after_bytes,
            stats_before_bytes,
            stats_after_bytes,
            hypertable_before_bytes,
            hypertable_after_bytes,
            ratio_percent)
        VALUES (
            scenario.ordinal,
            scenario.scenario,
            scenario.site_id,
            scenario.start_at,
            scenario.end_at,
            scenario.window_days,
            row_count_before,
            row_count_after,
            channel_count,
            series_cardinality,
            chunk_count,
            detailed_before,
            detailed_after,
            stats_before,
            stats_after,
            hypertable_before,
            hypertable_after,
            100.0 * detailed_after / detailed_before);
    END LOOP;
END
$measurement$;

-- Preserve diagnostic evidence even when the strict D1 gate is red. These rows are explicitly
-- labelled uncommitted; only the NVM_COMPRESSION_RESULT rows emitted after COMMIT are success proof.
SELECT format(
    'NVM_COMPRESSION_MEASUREMENT committed=false scenario=%s site_id=%s start_at=%s end_at=%s window_days=%s sample_period_seconds=5 rows_before=%s rows_after=%s channels=%s cardinality=%s chunks=%s detailed_before_bytes=%s detailed_after_bytes=%s stats_before_bytes=%s stats_after_bytes=%s hypertable_before_bytes=%s hypertable_after_bytes=%s ratio_percent=%s',
    report.scenario,
    report.site_id,
    to_char(report.start_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    to_char(report.end_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    report.window_days,
    report.rows_before,
    report.rows_after,
    report.channels,
    report.cardinality,
    report.chunks,
    report.detailed_before_bytes,
    report.detailed_after_bytes,
    report.stats_before_bytes,
    report.stats_after_bytes,
    report.hypertable_before_bytes,
    report.hypertable_after_bytes,
    to_char(report.ratio_percent, 'FM999990.000000'))
FROM c09_compression_report AS report
ORDER BY report.ordinal;

SELECT format(
    'NVM_COMPRESSION_MEASUREMENT_SUMMARY committed=false gate_status=pending site_id=NV1'
    || ' ratio_8_percent=%s ratio_40_percent=%s ratio_7d_at_40_percent=%s ratio_28d_at_40_percent=%s'
    || ' cardinality_axis_pp=%s volume_axis_pp=%s overall_spread_pp=%s volume_multiple=%s'
    || ' max_ratio_percent=15 max_difference_pp=2 min_volume_multiple=4',
    to_char(max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_8'), 'FM999990.000000'),
    to_char(max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_40'), 'FM999990.000000'),
    to_char(max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_7d_at_40'), 'FM999990.000000'),
    to_char(max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_28d_at_40'), 'FM999990.000000'),
    to_char(abs(
        max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_8')
        - max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_40')), 'FM999990.000000'),
    to_char(abs(
        max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_7d_at_40')
        - max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_28d_at_40')), 'FM999990.000000'),
    to_char(max(report.ratio_percent) - min(report.ratio_percent), 'FM999990.000000'),
    to_char(
        max(report.rows_before) FILTER (WHERE report.scenario = 'volume_28d_at_40')::NUMERIC
        / max(report.rows_before) FILTER (WHERE report.scenario = 'volume_7d_at_40'), 'FM999990.000000'))
FROM c09_compression_report AS report;

-- Five thresholds rather than two, so every branch can be driven red on its own by the negative
-- controls below. A gate whose branches cannot be shown to fire is a gate nobody has tested.
--
-- The same-cardinality requirement of D1 condition (3) is deliberately NOT asserted here: it is
-- already enforced upstream by the fixture fingerprint, which RAISEs when a window's channel count
-- differs from the pinned expected_channels. Asserting it twice would add a branch with no way to
-- prove it can fail.
CREATE OR REPLACE FUNCTION pg_temp.c09_assert_gate(
    allowed_ratio NUMERIC,
    allowed_overall_difference NUMERIC,
    allowed_cardinality_difference NUMERIC,
    allowed_volume_difference NUMERIC,
    required_volume_multiple NUMERIC)
RETURNS VOID
LANGUAGE plpgsql
AS $gate_function$
DECLARE
    measured_count BIGINT;
    worst_ratio NUMERIC;
    overall_difference NUMERIC;
    cardinality_difference NUMERIC;
    volume_difference NUMERIC;
    volume_multiple NUMERIC;
BEGIN
    SELECT count(*), max(report.ratio_percent), max(report.ratio_percent) - min(report.ratio_percent)
    INTO measured_count, worst_ratio, overall_difference
    FROM c09_compression_report AS report;

    SELECT abs(
               max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_8')
               - max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_40')),
           abs(
               max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_7d_at_40')
               - max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_28d_at_40')),
           max(report.rows_before) FILTER (WHERE report.scenario = 'volume_28d_at_40')::NUMERIC
               / max(report.rows_before) FILTER (WHERE report.scenario = 'volume_7d_at_40')
    INTO cardinality_difference, volume_difference, volume_multiple
    FROM c09_compression_report AS report;

    IF measured_count <> 4 THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P3090',
            MESSAGE = format(
                'C09 expected four scenarios across two axes, measured %s',
                measured_count);
    END IF;

    -- D1 condition (1): every ratio below the threshold.
    IF worst_ratio >= allowed_ratio THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P3091',
            MESSAGE = format(
                'C09 D1 failed: worst ratio %s percent is not below %s percent',
                worst_ratio,
                allowed_ratio);
    END IF;

    -- D1 condition (4): max minus min across every scenario, not only within a pair.
    IF overall_difference >= allowed_overall_difference THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P3092',
            MESSAGE = format(
                'C09 D1 failed: overall ratio spread %s pp is not below %s pp',
                overall_difference,
                allowed_overall_difference);
    END IF;

    -- D1 condition (2): the cardinality axis.
    IF cardinality_difference >= allowed_cardinality_difference THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P3093',
            MESSAGE = format(
                'C09 D1 failed: cardinality axis moved %s pp, which is not below %s pp',
                cardinality_difference,
                allowed_cardinality_difference);
    END IF;

    -- D1 condition (3), first half: the volume axis holds its ratio.
    IF volume_difference >= allowed_volume_difference THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P3094',
            MESSAGE = format(
                'C09 D1 failed: volume axis moved %s pp, which is not below %s pp',
                volume_difference,
                allowed_volume_difference);
    END IF;

    -- D1 condition (3), second half: the volume axis is actually an axis. Without this the two
    -- windows could drift towards each other in a later edit and the axis would quietly stop
    -- testing anything while still reporting two green numbers.
    IF volume_multiple < required_volume_multiple THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P3095',
            MESSAGE = format(
                'C09 D1 failed: the large volume tier is %s times the small one, below the required %s',
                volume_multiple,
                required_volume_multiple);
    END IF;
END
$gate_function$;

CREATE TEMP TABLE c09_negative_control (
    branch TEXT PRIMARY KEY,
    observed TEXT NOT NULL
) ON COMMIT PRESERVE ROWS;

-- One control per gate branch, each driven at its own boundary while every other threshold is
-- left permissive. Passing the observed value as the threshold is what makes these exact: the
-- comparison is >= (or < for the multiple), so equality is the first failing point.
DO $negative_control$
DECLARE
    permissive_ratio CONSTANT NUMERIC := 100;
    permissive_difference CONSTANT NUMERIC := 100;
    permissive_multiple CONSTANT NUMERIC := 0;
    worst_ratio NUMERIC;
    overall_difference NUMERIC;
    cardinality_difference NUMERIC;
    volume_difference NUMERIC;
    volume_multiple NUMERIC;
BEGIN
    SELECT max(report.ratio_percent),
           max(report.ratio_percent) - min(report.ratio_percent),
           abs(
               max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_8')
               - max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_40')),
           abs(
               max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_7d_at_40')
               - max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_28d_at_40')),
           max(report.rows_before) FILTER (WHERE report.scenario = 'volume_28d_at_40')::NUMERIC
               / max(report.rows_before) FILTER (WHERE report.scenario = 'volume_7d_at_40')
    INTO worst_ratio,
         overall_difference,
         cardinality_difference,
         volume_difference,
         volume_multiple
    FROM c09_compression_report AS report;

    BEGIN
        PERFORM pg_temp.c09_assert_gate(
            worst_ratio, permissive_difference, permissive_difference, permissive_difference, permissive_multiple);
        RAISE EXCEPTION 'C09 ratio equality negative control unexpectedly stayed green';
    EXCEPTION
        WHEN SQLSTATE 'P3091' THEN
            INSERT INTO c09_negative_control (branch, observed) VALUES ('ratio_equality', 'red');
    END;

    BEGIN
        PERFORM pg_temp.c09_assert_gate(
            permissive_ratio, overall_difference, permissive_difference, permissive_difference, permissive_multiple);
        RAISE EXCEPTION 'C09 overall difference equality negative control unexpectedly stayed green';
    EXCEPTION
        WHEN SQLSTATE 'P3092' THEN
            INSERT INTO c09_negative_control (branch, observed) VALUES ('overall_difference_equality', 'red');
    END;

    BEGIN
        PERFORM pg_temp.c09_assert_gate(
            permissive_ratio, permissive_difference, cardinality_difference, permissive_difference, permissive_multiple);
        RAISE EXCEPTION 'C09 cardinality axis equality negative control unexpectedly stayed green';
    EXCEPTION
        WHEN SQLSTATE 'P3093' THEN
            INSERT INTO c09_negative_control (branch, observed) VALUES ('cardinality_axis_equality', 'red');
    END;

    BEGIN
        PERFORM pg_temp.c09_assert_gate(
            permissive_ratio, permissive_difference, permissive_difference, volume_difference, permissive_multiple);
        RAISE EXCEPTION 'C09 volume axis equality negative control unexpectedly stayed green';
    EXCEPTION
        WHEN SQLSTATE 'P3094' THEN
            INSERT INTO c09_negative_control (branch, observed) VALUES ('volume_axis_equality', 'red');
    END;

    BEGIN
        PERFORM pg_temp.c09_assert_gate(
            permissive_ratio, permissive_difference, permissive_difference, permissive_difference, volume_multiple + 1);
        RAISE EXCEPTION 'C09 volume multiple negative control unexpectedly stayed green';
    EXCEPTION
        WHEN SQLSTATE 'P3095' THEN
            INSERT INTO c09_negative_control (branch, observed) VALUES ('volume_multiple', 'red');
    END;
END
$negative_control$;

SELECT format(
    'NVM_COMPRESSION_NEGATIVE_CONTROL committed=false branch=%s observed=%s',
    control.branch,
    control.observed)
FROM c09_negative_control AS control
ORDER BY control.branch;

-- Keep the real DoD assertion last. A red measurement retains diagnostic output above but rolls
-- compression back and never emits the committed NVM_COMPRESSION_RESULT records below.
SELECT pg_temp.c09_assert_gate(15, 2, 2, 2, 4);

COMMIT;

SELECT format(
    'NVM_COMPRESSION_RESULT scenario=%s site_id=%s start_at=%s end_at=%s window_days=%s sample_period_seconds=5 rows_before=%s rows_after=%s channels=%s cardinality=%s chunks=%s detailed_before_bytes=%s detailed_after_bytes=%s stats_before_bytes=%s stats_after_bytes=%s hypertable_before_bytes=%s hypertable_after_bytes=%s ratio_percent=%s',
    report.scenario,
    report.site_id,
    to_char(report.start_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    to_char(report.end_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    report.window_days,
    report.rows_before,
    report.rows_after,
    report.channels,
    report.cardinality,
    report.chunks,
    report.detailed_before_bytes,
    report.detailed_after_bytes,
    report.stats_before_bytes,
    report.stats_after_bytes,
    report.hypertable_before_bytes,
    report.hypertable_after_bytes,
    to_char(report.ratio_percent, 'FM999990.000000'))
FROM c09_compression_report AS report
ORDER BY report.ordinal;

SELECT format(
    'NVM_COMPRESSION_SUMMARY gate_status=pass site_id=NV1'
    || ' ratio_8_percent=%s ratio_40_percent=%s ratio_7d_at_40_percent=%s ratio_28d_at_40_percent=%s'
    || ' cardinality_axis_pp=%s volume_axis_pp=%s overall_spread_pp=%s volume_multiple=%s'
    || ' max_ratio_percent=15 max_difference_pp=2 min_volume_multiple=4',
    to_char(max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_8'), 'FM999990.000000'),
    to_char(max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_40'), 'FM999990.000000'),
    to_char(max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_7d_at_40'), 'FM999990.000000'),
    to_char(max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_28d_at_40'), 'FM999990.000000'),
    to_char(abs(
        max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_8')
        - max(report.ratio_percent) FILTER (WHERE report.scenario = 'channels_40')), 'FM999990.000000'),
    to_char(abs(
        max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_7d_at_40')
        - max(report.ratio_percent) FILTER (WHERE report.scenario = 'volume_28d_at_40')), 'FM999990.000000'),
    to_char(max(report.ratio_percent) - min(report.ratio_percent), 'FM999990.000000'),
    to_char(
        max(report.rows_before) FILTER (WHERE report.scenario = 'volume_28d_at_40')::NUMERIC
        / max(report.rows_before) FILTER (WHERE report.scenario = 'volume_7d_at_40'), 'FM999990.000000'))
FROM c09_compression_report AS report;

SELECT format(
    'NVM_COMPRESSION_NEGATIVE_CONTROL branch=%s observed=%s',
    control.branch,
    control.observed)
FROM c09_negative_control AS control
ORDER BY control.branch;
