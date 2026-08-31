\set ON_ERROR_STOP on

SET TIME ZONE 'UTC';
SET client_min_messages = warning;
SET lock_timeout = '30s';
SET statement_timeout = '30min';

-- One session at a time may recompress/refresh these pinned windows. The lock is released even
-- when ON_ERROR_STOP closes the session after an assertion.
SELECT pg_advisory_lock(hashtextextended('novavolt.c10.rollup-bench', 0));

CREATE FUNCTION pg_temp.c10_require(condition BOOLEAN, message TEXT, hint TEXT DEFAULT NULL)
RETURNS VOID
LANGUAGE plpgsql
AS $assert$
BEGIN
    IF condition IS DISTINCT FROM true THEN
        IF hint IS NULL THEN
            RAISE EXCEPTION USING MESSAGE = message;
        ELSE
            RAISE EXCEPTION USING MESSAGE = message, HINT = hint;
        END IF;
    END IF;
END
$assert$;

CREATE TEMP TABLE c10_window (
    window_kind TEXT        PRIMARY KEY,
    start_at    TIMESTAMPTZ NOT NULL,
    end_at      TIMESTAMPTZ NOT NULL,
    CHECK (start_at < end_at)
);

INSERT INTO c10_window (window_kind, start_at, end_at)
VALUES
    ('target', TIMESTAMPTZ '2026-07-20 00:00:00Z', TIMESTAMPTZ '2026-07-27 00:00:00Z'),
    ('far',    TIMESTAMPTZ '2026-08-16 00:00:00Z', TIMESTAMPTZ '2026-08-21 00:00:00Z');

CREATE TEMP TABLE c10_expected_channel (
    equipment_id TEXT PRIMARY KEY
);

INSERT INTO c10_expected_channel (equipment_id)
SELECT 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-' || lpad(channel::TEXT, 4, '0')
FROM generate_series(1, 100) AS expected(channel);

CREATE TEMP TABLE c10_fixture_fingerprint AS
WITH target AS (
    SELECT start_at, end_at
    FROM c10_window
    WHERE window_kind = 'target'
), actual_channel AS (
    SELECT DISTINCT measurements.equipment_id
    FROM ts.telemetry_measurement AS measurements
    CROSS JOIN target
    WHERE measurements.site_id = 'NV1'
      AND measurements.device_timestamp >= target.start_at
      AND measurements.device_timestamp < target.end_at
      AND measurements.signal_code = 'Formation/Temperature'
      AND measurements.value_kind = 'real'
), missing_channel AS (
    SELECT expected.equipment_id
    FROM c10_expected_channel AS expected
    EXCEPT
    SELECT actual.equipment_id
    FROM actual_channel AS actual
), unexpected_channel AS (
    SELECT actual.equipment_id
    FROM actual_channel AS actual
    EXCEPT
    SELECT expected.equipment_id
    FROM c10_expected_channel AS expected
)
SELECT count(*) AS temperature_rows,
       count(DISTINCT measurements.equipment_id) AS channels,
       count(DISTINCT time_bucket(INTERVAL '1 minute', measurements.device_timestamp)) AS machine_buckets,
       count(*) FILTER (WHERE measurements.clock_quality = 'Good') AS good_rows,
       count(*) FILTER (WHERE measurements.clock_quality <> 'Good') AS non_good_rows,
       count(*) FILTER (
           WHERE measurements.equipment_id =
               'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001') AS channel_0001_rows,
       count(DISTINCT time_bucket(INTERVAL '1 minute', measurements.device_timestamp)) FILTER (
           WHERE measurements.equipment_id =
               'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001') AS channel_0001_buckets,
       min(measurements.device_timestamp) AS first_sample_at,
       max(measurements.device_timestamp) AS last_sample_at,
       (SELECT count(*) FROM missing_channel) AS missing_channels,
       (SELECT count(*) FROM unexpected_channel) AS unexpected_channels
FROM ts.telemetry_measurement AS measurements
CROSS JOIN target
WHERE measurements.site_id = 'NV1'
  AND measurements.device_timestamp >= target.start_at
  AND measurements.device_timestamp < target.end_at
  AND measurements.signal_code = 'Formation/Temperature'
  AND measurements.value_kind = 'real';

SELECT pg_temp.c10_require(
    fingerprint.temperature_rows = 121429
    AND fingerprint.channels = 100
    AND fingerprint.machine_buckets = 10080
    AND fingerprint.good_rows = 121429
    AND fingerprint.non_good_rows = 0
    AND fingerprint.channel_0001_rows = 1172
    AND fingerprint.channel_0001_buckets = 965
    AND fingerprint.first_sample_at = TIMESTAMPTZ '2026-07-20 00:00:00Z'
    AND fingerprint.last_sample_at = TIMESTAMPTZ '2026-07-26 23:59:50Z'
    AND fingerprint.missing_channels = 0
    AND fingerprint.unexpected_channels = 0,
    format(
        'C10 target fixture fingerprint mismatch: expected Temperature rows=121429, channels=100, buckets=10080, Good=121429, non-Good=0, CH-0001 rows/buckets=1172/965, range=[2026-07-20 00:00:00Z, 2026-07-26 23:59:50Z], missing=0, unexpected=0; found rows=%s, channels=%s, buckets=%s, Good=%s, non-Good=%s, CH-0001=%s/%s, range=[%s, %s], missing=%s, unexpected=%s',
        fingerprint.temperature_rows,
        fingerprint.channels,
        fingerprint.machine_buckets,
        fingerprint.good_rows,
        fingerprint.non_good_rows,
        fingerprint.channel_0001_rows,
        fingerprint.channel_0001_buckets,
        fingerprint.first_sample_at,
        fingerprint.last_sample_at,
        fingerprint.missing_channels,
        fingerprint.unexpected_channels),
    'Run once: make telemetry-backfill CHANNELS=100 DAYS=7 END_AT=2026-07-27T00:00:00Z SAMPLE_PERIOD_SECONDS=5 DRIFTED_RATE=0')
FROM c10_fixture_fingerprint AS fingerprint;

-- C09 leaves a stable five-day fixture far from the target. Refreshing it creates a materialized
-- partition that the target EXPLAIN must exclude; without that control, "one chunk was read" would
-- not prove that an unrelated chunk was pruned.
SELECT pg_temp.c10_require(
    EXISTS (
        SELECT 1
        FROM ts.telemetry_measurement AS measurements
        JOIN c10_window AS far
          ON far.window_kind = 'far'
         AND measurements.device_timestamp >= far.start_at
         AND measurements.device_timestamp < far.end_at
        WHERE measurements.site_id = 'NV1'
          AND measurements.signal_code = 'Formation/Temperature'
          AND measurements.value_kind = 'real'),
    'C10 far-control fixture [2026-08-16, 2026-08-21) is empty.',
    'Run C08/C09 first; this is the inner five-day window of the 8-channel C09 fixture.');

CREATE TEMP TABLE c10_materialization AS
SELECT aggregates.materialization_hypertable_schema AS schema_name,
       aggregates.materialization_hypertable_name AS table_name
FROM timescaledb_information.continuous_aggregates AS aggregates
WHERE aggregates.view_schema = 'ts'
  AND aggregates.view_name = 'process_signal_1m'
  AND aggregates.hypertable_schema = 'ts'
  AND aggregates.hypertable_name = 'telemetry_measurement'
  AND aggregates.materialized_only;

SELECT pg_temp.c10_require(
    (SELECT count(*) FROM c10_materialization) = 1,
    'ts.process_signal_1m is missing, is not materialized-only, or is not sourced from ts.telemetry_measurement.',
    'Run: make ingestion-migrate');

-- End every run at the same physical state. Recompression also folds any rowstore tail left by a
-- previous late insert back into the compressed segment before raw and rollup timings are compared.
BEGIN;
LOCK TABLE ts.telemetry_measurement IN SHARE MODE;

SELECT pg_temp.c10_require(
    (
        SELECT count(*)
        FROM timescaledb_information.chunks AS chunks
        JOIN c10_window AS target
          ON target.window_kind = 'target'
         AND chunks.range_start >= target.start_at
         AND chunks.range_end <= target.end_at
        WHERE chunks.hypertable_schema = 'ts'
          AND chunks.hypertable_name = 'telemetry_measurement'
    ) = 7,
    'C10 target must occupy exactly seven exclusive one-day raw chunks.');

DO $compression$
DECLARE
    target RECORD;
BEGIN
    FOR target IN
        SELECT format('%I.%I', chunks.chunk_schema, chunks.chunk_name)::REGCLASS AS chunk,
               catalogue.status
        FROM timescaledb_information.chunks AS chunks
        JOIN c10_window AS benchmark
          ON benchmark.window_kind = 'target'
         AND chunks.range_start >= benchmark.start_at
         AND chunks.range_end <= benchmark.end_at
        JOIN _timescaledb_catalog.chunk AS catalogue
          ON catalogue.relid = format('%I.%I', chunks.chunk_schema, chunks.chunk_name)::REGCLASS
        WHERE chunks.hypertable_schema = 'ts'
          AND chunks.hypertable_name = 'telemetry_measurement'
        ORDER BY chunks.range_start
    LOOP
        -- Chi nen chunk NAO CHUA o `status = 1`. Truoc day dong nay goi `recompress => true` vo
        -- dieu kien, nen moi lan `make rollup-bench` nen lai bay chunk thang 7 du chung da nen
        -- xong. Do duoc 2026-09-01: rieng khoi nay chay ~10 phut moi lan, lam lai viec da lam.
        --
        -- `status` o `_timescaledb_catalog.chunk`: 0 chua nen · 1 nen day du · 3 nen nhung unordered
        -- · 9 nen nhung con duoi rowstore · 11 ca hai. Chi 1 la trang thai on dinh, va do la trang
        -- thai khoi nay ton tai de dat toi — nen dat toi roi thi khong lam gi nua.
        IF target.status <> 1 THEN
            PERFORM compress_chunk(target.chunk, if_not_compressed => true, recompress => true);
        END IF;
    END LOOP;
END
$compression$;

SELECT pg_temp.c10_require(
    (
        SELECT count(*) FILTER (WHERE chunks.is_compressed)
        FROM timescaledb_information.chunks AS chunks
        JOIN c10_window AS target
          ON target.window_kind = 'target'
         AND chunks.range_start >= target.start_at
         AND chunks.range_end <= target.end_at
        WHERE chunks.hypertable_schema = 'ts'
          AND chunks.hypertable_name = 'telemetry_measurement'
    ) = 7,
    'C10 failed to normalize all seven target raw chunks to the compressed state.');

COMMIT;

CALL refresh_continuous_aggregate(
    'ts.process_signal_1m',
    TIMESTAMPTZ '2026-07-20 00:00:00Z',
    TIMESTAMPTZ '2026-07-27 00:00:00Z',
    force => true);

CALL refresh_continuous_aggregate(
    'ts.process_signal_1m',
    TIMESTAMPTZ '2026-08-16 00:00:00Z',
    TIMESTAMPTZ '2026-08-21 00:00:00Z',
    force => true);

-- Child SAU parent, va no phai o day chu khong o dau khac.
--
-- Migration 013 tao ts.process_signal_machine_1m `WITH NO DATA`. Truoc khi co hai lenh nay, phep do
-- muc may chi refresh parent, con scenario `rollup_machine_level` khong doi child phai co du lieu.
-- Tren may nay child dang co 277.867 row nen con so 8,080 ms la that — nhung tren MOT CLEAN
-- DEPLOYMENT, child rong, truy van tra ve 0 dong trong khoang mot mili-giay, va gate `p95 < 200`
-- xanh. Mot gate xanh vi khong co gi de doc la gate te nhat co the co: no bao dung cai no duoc hoi
-- va sai cai nguoi ta muon biet.
--
-- Thu tu parent -> child la bat buoc, khong phai cho gon: child doc TU parent, nen refresh child
-- truoc se materialize mot khoang ma parent chua chot.
CALL refresh_continuous_aggregate(
    'ts.process_signal_machine_1m',
    TIMESTAMPTZ '2026-07-20 00:00:00Z',
    TIMESTAMPTZ '2026-07-27 00:00:00Z',
    force => true);

CALL refresh_continuous_aggregate(
    'ts.process_signal_machine_1m',
    TIMESTAMPTZ '2026-08-16 00:00:00Z',
    TIMESTAMPTZ '2026-08-21 00:00:00Z',
    force => true);

-- Child phai KHAC RONG va phai NOI CUNG MOT DIEU voi duong muc kenh, kiem truoc khi bat dau bam gio.
-- Hai ve, va ca hai deu can:
--   * bucket > 0    — chan dung truong hop "nhanh vi khong co gi de doc";
--   * lech = 0      — chan truong hop "nhanh vi tra loi sai".
-- Trung binh so sanh sau khi lam tron 6 chu so, dung ly do da ghi o rollup-bench-line.sql: `Gather`
-- gop tong so thuc theo thu tu khong co dinh, va mot ULP khong phai mot khac biet ve du lieu.
CREATE TEMP TABLE c10_child_equivalence AS
WITH target AS (
    SELECT start_at, end_at FROM c10_window WHERE window_kind = 'target'
), parent_side AS (
    SELECT rollup.bucket,
           sum(rollup.sample_count) AS samples,
           sum(rollup.avg_value * rollup.sample_count) AS weighted
    FROM ts.process_signal_1m AS rollup
    CROSS JOIN target
    WHERE rollup.site_id = 'NV1'
      AND rollup.equipment_id LIKE 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/%'
      AND rollup.signal_code = 'Formation/Temperature'
      AND rollup.bucket >= target.start_at
      AND rollup.bucket < target.end_at
    GROUP BY rollup.bucket
), child_side AS (
    SELECT machine.bucket,
           machine.sample_count AS samples,
           machine.avg_value * machine.sample_count AS weighted
    FROM ts.process_signal_machine_1m AS machine
    CROSS JOIN target
    WHERE machine.site_id = 'NV1'
      AND machine.machine_id = 'NOVAVOLT/NV1/FORMATION/F1/FORM-01'
      AND machine.signal_code = 'Formation/Temperature'
      AND machine.bucket >= target.start_at
      AND machine.bucket < target.end_at
)
SELECT count(*) FILTER (WHERE child_side.bucket IS NOT NULL) AS child_buckets,
       count(*) FILTER (WHERE parent_side.bucket IS NOT NULL) AS parent_buckets,
       count(*) FILTER (WHERE child_side.bucket IS NULL) AS parent_only,
       count(*) FILTER (WHERE parent_side.bucket IS NULL) AS child_only,
       count(*) FILTER (
           WHERE parent_side.bucket IS NOT NULL
             AND child_side.bucket IS NOT NULL
             AND parent_side.samples IS DISTINCT FROM child_side.samples) AS sample_mismatches,
       count(*) FILTER (
           WHERE parent_side.bucket IS NOT NULL
             AND child_side.bucket IS NOT NULL
             AND round(parent_side.weighted::NUMERIC, 6)
                 IS DISTINCT FROM round(child_side.weighted::NUMERIC, 6)) AS value_mismatches
FROM parent_side
FULL JOIN child_side ON child_side.bucket = parent_side.bucket;

SELECT pg_temp.c10_require(
    equivalence.child_buckets > 0
    AND equivalence.child_buckets = equivalence.parent_buckets
    AND equivalence.parent_only = 0
    AND equivalence.child_only = 0
    AND equivalence.sample_mismatches = 0
    AND equivalence.value_mismatches = 0,
    format(
        'C10 machine rollup does not match the channel path: child_buckets=%s parent_buckets=%s parent_only=%s child_only=%s sample_mismatch=%s value_mismatch=%s',
        equivalence.child_buckets,
        equivalence.parent_buckets,
        equivalence.parent_only,
        equivalence.child_only,
        equivalence.sample_mismatches,
        equivalence.value_mismatches),
    'An empty child makes the machine-level scenario fast for the wrong reason; migration 013 creates it WITH NO DATA.')
FROM c10_child_equivalence AS equivalence;

SELECT format(
    'NVM_ROLLUP_CHILD_EQUIVALENCE child_buckets=%s parent_buckets=%s parent_only=%s child_only=%s sample_mismatch=%s value_mismatch=%s',
    equivalence.child_buckets,
    equivalence.parent_buckets,
    equivalence.parent_only,
    equivalence.child_only,
    equivalence.sample_mismatches,
    equivalence.value_mismatches)
FROM c10_child_equivalence AS equivalence;

CREATE TEMP TABLE c10_equivalence_detail AS
WITH target AS (
    SELECT start_at, end_at
    FROM c10_window
    WHERE window_kind = 'target'
), configured_scope AS (
    SELECT 'channel'::TEXT AS scope_name,
           'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001'::TEXT AS channel_id
    UNION ALL
    SELECT 'machine', NULL::TEXT
), raw AS (
    SELECT configured.scope_name,
           time_bucket(INTERVAL '1 minute', measurements.device_timestamp) AS bucket,
           count(*) AS samples,
           sum(measurements.real_value::NUMERIC) AS weighted_value
    FROM configured_scope AS configured
    CROSS JOIN target
    JOIN ts.telemetry_measurement AS measurements
      ON measurements.device_timestamp >= target.start_at
     AND measurements.device_timestamp < target.end_at
     AND (configured.scope_name = 'machine' OR measurements.equipment_id = configured.channel_id)
    WHERE measurements.site_id = 'NV1'
      AND measurements.signal_code = 'Formation/Temperature'
      AND measurements.value_kind = 'real'
      AND measurements.equipment_id LIKE 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-%'
    GROUP BY configured.scope_name, bucket
), rollup AS (
    SELECT configured.scope_name,
           rollup.bucket,
           coalesce(sum(rollup.sample_count), 0)::NUMERIC AS samples,
           coalesce(sum(rollup.avg_value::NUMERIC * rollup.sample_count), 0) AS weighted_value
    FROM configured_scope AS configured
    CROSS JOIN target
    JOIN ts.process_signal_1m AS rollup
      ON rollup.bucket >= target.start_at
     AND rollup.bucket < target.end_at
     AND (configured.scope_name = 'machine' OR rollup.equipment_id = configured.channel_id)
    WHERE rollup.site_id = 'NV1'
      AND rollup.signal_code = 'Formation/Temperature'
      AND rollup.equipment_id LIKE 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-%'
    GROUP BY configured.scope_name, rollup.bucket
)
SELECT coalesce(raw.scope_name, rollup.scope_name) AS scope_name,
       coalesce(raw.bucket, rollup.bucket) AS bucket,
       raw.scope_name IS NOT NULL AS raw_present,
       rollup.scope_name IS NOT NULL AS rollup_present,
       raw.samples AS raw_samples,
       rollup.samples AS rollup_samples,
       raw.weighted_value AS raw_weighted_value,
       rollup.weighted_value AS rollup_weighted_value,
       CASE
           WHEN raw.scope_name IS NOT NULL AND rollup.scope_name IS NOT NULL
               THEN abs(raw.weighted_value - rollup.weighted_value)
           ELSE NULL
       END AS weighted_delta
FROM raw
FULL JOIN rollup
  ON rollup.scope_name = raw.scope_name
 AND rollup.bucket = raw.bucket;

CREATE UNIQUE INDEX c10_equivalence_detail_scope_bucket
    ON c10_equivalence_detail (scope_name, bucket);

CREATE TEMP TABLE c10_equivalence AS
SELECT detail.scope_name,
       count(*) FILTER (WHERE detail.raw_present) AS raw_buckets,
       count(*) FILTER (WHERE detail.rollup_present) AS rollup_buckets,
       coalesce(sum(detail.raw_samples), 0) AS raw_samples,
       coalesce(sum(detail.rollup_samples), 0) AS rollup_samples,
       coalesce(sum(detail.raw_weighted_value), 0) AS raw_weighted_value,
       coalesce(sum(detail.rollup_weighted_value), 0) AS rollup_weighted_value,
       abs(
           coalesce(sum(detail.raw_weighted_value), 0)
           - coalesce(sum(detail.rollup_weighted_value), 0)) AS weighted_delta,
       count(*) FILTER (WHERE detail.raw_present AND NOT detail.rollup_present) AS missing_in_rollup,
       count(*) FILTER (WHERE detail.rollup_present AND NOT detail.raw_present) AS extra_in_rollup,
       count(*) FILTER (
           WHERE detail.raw_present
             AND detail.rollup_present
             AND detail.raw_samples <> detail.rollup_samples) AS sample_mismatch_buckets,
       count(*) FILTER (
           WHERE detail.raw_present
             AND detail.rollup_present
             AND detail.weighted_delta
                 > greatest(1::NUMERIC, abs(detail.raw_weighted_value)) * 0.000000000001)
           AS weighted_mismatch_buckets,
       max(detail.weighted_delta) FILTER (
           WHERE detail.raw_present AND detail.rollup_present) AS max_weighted_delta
FROM c10_equivalence_detail AS detail
GROUP BY detail.scope_name;

SELECT pg_temp.c10_require(
    (SELECT count(*) FROM c10_equivalence) = 2,
    'C10 raw/rollup comparison did not produce both channel and machine scopes.');

SELECT pg_temp.c10_require(
    equivalence.raw_buckets = equivalence.rollup_buckets
    AND equivalence.raw_samples = equivalence.rollup_samples
    AND equivalence.missing_in_rollup = 0
    AND equivalence.extra_in_rollup = 0
    AND equivalence.sample_mismatch_buckets = 0
    AND equivalence.weighted_mismatch_buckets = 0,
    format(
        'C10 %s raw/rollup bucket mismatch: buckets=%s/%s samples=%s/%s missing=%s extra=%s sample_mismatch=%s weighted_mismatch=%s max_delta=%s total_weighted_delta=%s',
        equivalence.scope_name,
        equivalence.raw_buckets,
        equivalence.rollup_buckets,
        equivalence.raw_samples,
        equivalence.rollup_samples,
        equivalence.missing_in_rollup,
        equivalence.extra_in_rollup,
        equivalence.sample_mismatch_buckets,
        equivalence.weighted_mismatch_buckets,
        equivalence.max_weighted_delta,
        equivalence.weighted_delta))
FROM c10_equivalence AS equivalence;

CREATE TEMP TABLE c10_scenario (
    ordinal       INTEGER PRIMARY KEY,
    scenario_name TEXT    NOT NULL UNIQUE,
    source_name   TEXT    NOT NULL CHECK (source_name IN ('rollup', 'machine_rollup', 'raw')),
    scope_name    TEXT    NOT NULL CHECK (scope_name IN ('channel', 'machine')),
    enforce_gate  BOOLEAN NOT NULL,
    benchmark_sql TEXT    NOT NULL
);

INSERT INTO c10_scenario (
    ordinal,
    scenario_name,
    source_name,
    scope_name,
    enforce_gate,
    benchmark_sql)
VALUES
    (1, 'rollup_channel', 'rollup', 'channel', true, $query$
        WITH minute_temperature AS (
            SELECT rollup.bucket,
                   rollup.sample_count AS samples,
                   rollup.avg_value AS avg_temperature
            FROM ts.process_signal_1m AS rollup
            WHERE rollup.site_id = 'NV1'
              AND rollup.equipment_id = 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001'
              AND rollup.signal_code = 'Formation/Temperature'
              AND rollup.bucket >= TIMESTAMPTZ '2026-07-20 00:00:00Z'
              AND rollup.bucket < TIMESTAMPTZ '2026-07-27 00:00:00Z'
        )
        SELECT md5(concat_ws(
            '|',
            count(*),
            coalesce(sum(minute.samples), 0),
            round(coalesce(sum(minute.avg_temperature * minute.samples), 0)::NUMERIC, 9),
            coalesce(sum(extract(epoch FROM minute.bucket)), 0)))
        FROM minute_temperature AS minute
        $query$),
    -- Cau hoi cua D2 — "nhiet do trung binh moi phut cua FORM-01" — la mot cau hoi ve MAY, va tu
    -- migration 013 he thong luu san cau tra loi do. Day la ve duoc GATE.
    --
    -- Do duoc 2026-09-01: cua so thang 7 (chi co FORM-01) ~1,0-1,5 ms, cua so thang 5 (co du 10
    -- cycler cua line) ~0,95-1,0 ms. Hai con so BANG NHAU, va do moi la tinh chat D2 can: cau tra
    -- loi khong con phu thuoc vao viec trong database con co gi khac. Doi chieu tung bucket voi
    -- duong muc kenh: 10.080 va 3.488 bucket, lech 0 o ca sample_count lan trung binh.
    (2, 'rollup_machine_level', 'machine_rollup', 'machine', true, $query$
        WITH minute_temperature AS (
            SELECT rollup.bucket,
                   rollup.sample_count AS samples,
                   rollup.avg_value AS avg_temperature
            FROM ts.process_signal_machine_1m AS rollup
            WHERE rollup.site_id = 'NV1'
              AND rollup.machine_id = 'NOVAVOLT/NV1/FORMATION/F1/FORM-01'
              AND rollup.signal_code = 'Formation/Temperature'
              AND rollup.bucket >= TIMESTAMPTZ '2026-07-20 00:00:00Z'
              AND rollup.bucket < TIMESTAMPTZ '2026-07-27 00:00:00Z'
        )
        SELECT md5(concat_ws(
            '|',
            count(*),
            coalesce(sum(minute.samples), 0),
            round(coalesce(sum(minute.avg_temperature * minute.samples), 0)::NUMERIC, 6),
            coalesce(sum(extract(epoch FROM minute.bucket)), 0)))
        FROM minute_temperature AS minute
        $query$),
    -- Duong CU, gop 100 kenh luc doc. Giu lai va KHONG gate nua: plan section C10 doi ghi CA HAI
    -- bang so, va con so nay la thu cho biet rollup tang hai mua duoc gi. No cung la con so lo ra
    -- rang D2 tung xanh chi vi cua so nay chi chua mot may — trong cua so co du line no la
    -- 1.312,404 ms.
    (3, 'rollup_machine', 'rollup', 'machine', false, $query$
        WITH minute_temperature AS (
            SELECT rollup.bucket,
                   sum(rollup.sample_count)::BIGINT AS samples,
                   sum(rollup.avg_value * rollup.sample_count::DOUBLE PRECISION)
                       / sum(rollup.sample_count)::DOUBLE PRECISION AS avg_temperature
            FROM ts.process_signal_1m AS rollup
            WHERE rollup.site_id = 'NV1'
              AND rollup.equipment_id LIKE 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-%'
              AND rollup.signal_code = 'Formation/Temperature'
              AND rollup.bucket >= TIMESTAMPTZ '2026-07-20 00:00:00Z'
              AND rollup.bucket < TIMESTAMPTZ '2026-07-27 00:00:00Z'
            GROUP BY rollup.bucket
        )
        SELECT md5(concat_ws(
            '|',
            count(*),
            coalesce(sum(minute.samples), 0),
            round(coalesce(sum(minute.avg_temperature * minute.samples), 0)::NUMERIC, 9),
            coalesce(sum(extract(epoch FROM minute.bucket)), 0)))
        FROM minute_temperature AS minute
        $query$),
    (4, 'raw_channel', 'raw', 'channel', false, $query$
        WITH minute_temperature AS (
            SELECT time_bucket(INTERVAL '1 minute', measurements.device_timestamp) AS bucket,
                   count(*) AS samples,
                   avg(measurements.real_value) AS avg_temperature
            FROM ts.telemetry_measurement AS measurements
            WHERE measurements.site_id = 'NV1'
              AND measurements.equipment_id = 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001'
              AND measurements.signal_code = 'Formation/Temperature'
              AND measurements.value_kind = 'real'
              AND measurements.device_timestamp >= TIMESTAMPTZ '2026-07-20 00:00:00Z'
              AND measurements.device_timestamp < TIMESTAMPTZ '2026-07-27 00:00:00Z'
            GROUP BY bucket
        )
        SELECT md5(concat_ws(
            '|',
            count(*),
            coalesce(sum(minute.samples), 0),
            round(coalesce(sum(minute.avg_temperature * minute.samples), 0)::NUMERIC, 9),
            coalesce(sum(extract(epoch FROM minute.bucket)), 0)))
        FROM minute_temperature AS minute
        $query$),
    (5, 'raw_machine', 'raw', 'machine', false, $query$
        WITH minute_temperature AS (
            SELECT time_bucket(INTERVAL '1 minute', measurements.device_timestamp) AS bucket,
                   count(*) AS samples,
                   avg(measurements.real_value) AS avg_temperature
            FROM ts.telemetry_measurement AS measurements
            WHERE measurements.site_id = 'NV1'
              AND measurements.equipment_id LIKE 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-%'
              AND measurements.signal_code = 'Formation/Temperature'
              AND measurements.value_kind = 'real'
              AND measurements.device_timestamp >= TIMESTAMPTZ '2026-07-20 00:00:00Z'
              AND measurements.device_timestamp < TIMESTAMPTZ '2026-07-27 00:00:00Z'
            GROUP BY bucket
        )
        SELECT md5(concat_ws(
            '|',
            count(*),
            coalesce(sum(minute.samples), 0),
            round(coalesce(sum(minute.avg_temperature * minute.samples), 0)::NUMERIC, 9),
            coalesce(sum(extract(epoch FROM minute.bucket)), 0)))
        FROM minute_temperature AS minute
        $query$);

CREATE TEMP TABLE c10_timing (
    scenario_name TEXT    NOT NULL,
    run_number    INTEGER NOT NULL,
    warmup        BOOLEAN NOT NULL,
    elapsed_ms    NUMERIC NOT NULL CHECK (elapsed_ms > 0),
    checksum      TEXT    NOT NULL,
    PRIMARY KEY (scenario_name, run_number)
);

DO $timing$
DECLARE
    scenario RECORD;
    run INTEGER;
    started TIMESTAMPTZ;
    observed_checksum TEXT;
BEGIN
    -- Warm every plan/data path once before sampling. Timed runs are then interleaved so one source
    -- does not receive all of an early or late machine state.
    FOR scenario IN SELECT * FROM c10_scenario ORDER BY ordinal
    LOOP
        started := clock_timestamp();
        EXECUTE scenario.benchmark_sql INTO STRICT observed_checksum;
        INSERT INTO c10_timing (scenario_name, run_number, warmup, elapsed_ms, checksum)
        VALUES (
            scenario.scenario_name,
            0,
            true,
            extract(epoch FROM clock_timestamp() - started) * 1000,
            observed_checksum);
    END LOOP;

    FOR run IN 1..10
    LOOP
        -- Rotate the starting scenario on every run. Each source/scope therefore occupies every
        -- early/late position instead of rollup_channel always receiving the first cache/CPU state.
        FOR scenario IN
            SELECT *
            FROM c10_scenario
            ORDER BY mod(ordinal - run + 50, 5)
        LOOP
            started := clock_timestamp();
            EXECUTE scenario.benchmark_sql INTO STRICT observed_checksum;
            INSERT INTO c10_timing (scenario_name, run_number, warmup, elapsed_ms, checksum)
            VALUES (
                scenario.scenario_name,
                run,
                false,
                extract(epoch FROM clock_timestamp() - started) * 1000,
                observed_checksum);
        END LOOP;
    END LOOP;
END
$timing$;

SELECT pg_temp.c10_require(
    (SELECT count(*) FROM c10_timing) = 55
    AND NOT EXISTS (
        SELECT scenarios.scenario_name, expected.run_number
        FROM c10_scenario AS scenarios
        CROSS JOIN generate_series(0, 10) AS expected(run_number)
        EXCEPT
        SELECT timing.scenario_name, timing.run_number
        FROM c10_timing AS timing)
    AND NOT EXISTS (
        SELECT 1
        FROM c10_timing AS timing
        WHERE timing.warmup IS DISTINCT FROM (timing.run_number = 0)),
    'C10 timing must contain exactly one warmup and runs 1..10 for every scenario.');

SELECT pg_temp.c10_require(
    stable.measured_runs = 10
    AND stable.distinct_checksums = 1
    AND stable.warmup_checksum = stable.measured_checksum,
    format(
        'C10 %s timing/checksum invariant failed: measured=%s distinct=%s warmup=%s measured_checksum=%s',
        stable.scenario_name,
        stable.measured_runs,
        stable.distinct_checksums,
        stable.warmup_checksum,
        stable.measured_checksum))
FROM (
    SELECT timing.scenario_name,
           count(*) FILTER (WHERE NOT timing.warmup) AS measured_runs,
           count(DISTINCT timing.checksum) FILTER (WHERE NOT timing.warmup) AS distinct_checksums,
           min(timing.checksum) FILTER (WHERE timing.warmup) AS warmup_checksum,
           min(timing.checksum) FILTER (WHERE NOT timing.warmup) AS measured_checksum
    FROM c10_timing AS timing
    GROUP BY timing.scenario_name
) AS stable;

CREATE TEMP TABLE c10_summary AS
SELECT scenario.ordinal,
       scenario.scenario_name,
       scenario.source_name,
       scenario.scope_name,
       scenario.enforce_gate,
       -- With only ten prescribed samples, nearest-rank p95 is the tenth (worst) observation.
       -- That makes the strict D2 gate conservative and avoids interpolating a synthetic latency.
       percentile_disc(0.50) WITHIN GROUP (ORDER BY timing.elapsed_ms) AS p50_ms,
       percentile_disc(0.95) WITHIN GROUP (ORDER BY timing.elapsed_ms) AS p95_ms,
       max(timing.elapsed_ms) AS max_ms,
       min(timing.checksum) AS checksum,
       count(*) AS measured_runs
FROM c10_scenario AS scenario
JOIN c10_timing AS timing
  ON timing.scenario_name = scenario.scenario_name
 AND NOT timing.warmup
GROUP BY scenario.ordinal,
         scenario.scenario_name,
         scenario.source_name,
         scenario.scope_name,
         scenario.enforce_gate;

-- Capture plans separately from the ten samples: EXPLAIN ANALYZE adds instrumentation overhead, so
-- its execution time is structural evidence and never substituted for the D2 timing distribution.
CREATE TEMP TABLE c10_plan (
    scenario_name TEXT  PRIMARY KEY,
    plan          JSONB NOT NULL
);

DO $explain$
DECLARE
    scenario RECORD;
    explained JSONB;
BEGIN
    FOR scenario IN SELECT * FROM c10_scenario ORDER BY ordinal
    LOOP
        EXECUTE 'EXPLAIN (ANALYZE, BUFFERS, VERBOSE, FORMAT JSON) ' || scenario.benchmark_sql
        INTO STRICT explained;
        INSERT INTO c10_plan (scenario_name, plan)
        VALUES (scenario.scenario_name, explained);
    END LOOP;
END
$explain$;

CREATE FUNCTION pg_temp.c10_plan_nodes(document JSONB)
RETURNS TABLE (node JSONB)
LANGUAGE sql
IMMUTABLE
AS $nodes$
    WITH RECURSIVE expanded(node) AS (
        SELECT document -> 0 -> 'Plan'
        UNION ALL
        SELECT child.value
        FROM expanded AS parent
        CROSS JOIN LATERAL jsonb_array_elements(
            coalesce(parent.node -> 'Plans', '[]'::JSONB)) AS child(value)
    )
    SELECT expanded.node
    FROM expanded
$nodes$;

CREATE TEMP TABLE c10_plan_relation AS
SELECT plans.scenario_name,
       nodes.node ->> 'Schema' AS schema_name,
       nodes.node ->> 'Relation Name' AS table_name,
       nodes.node ->> 'Node Type' AS node_type
FROM c10_plan AS plans
CROSS JOIN LATERAL pg_temp.c10_plan_nodes(plans.plan) AS nodes
WHERE nodes.node ? 'Relation Name';

-- Map both a logical Timescale chunk and its compressed physical relation. A raw plan over a
-- compressed chunk names the latter below DecompressChunk; treating only the logical name as the
-- oracle would report a false absence of raw reads.
CREATE TEMP TABLE c10_chunk_relation (
    source_name      TEXT    NOT NULL,
    window_kind      TEXT    NOT NULL,
    logical_chunk_id INTEGER NOT NULL,
    logical_chunk    TEXT    NOT NULL,
    schema_name      TEXT    NOT NULL,
    table_name       TEXT    NOT NULL,
    PRIMARY KEY (source_name, schema_name, table_name)
);

WITH raw_chunks AS (
    SELECT catalog_chunk.id AS logical_chunk_id,
           format('%I.%I', chunks.chunk_schema, chunks.chunk_name) AS logical_chunk,
           chunks.chunk_schema AS schema_name,
           chunks.chunk_name AS table_name,
           CASE
               WHEN chunks.range_end > target.start_at AND chunks.range_start < target.end_at
                   THEN 'target'
               WHEN chunks.range_end > far.start_at AND chunks.range_start < far.end_at
                   THEN 'far'
               ELSE 'outside'
           END AS window_kind
    FROM timescaledb_information.chunks AS chunks
    JOIN pg_namespace AS chunk_namespace
      ON chunk_namespace.nspname = chunks.chunk_schema
    JOIN pg_class AS chunk_relation
      ON chunk_relation.relnamespace = chunk_namespace.oid
     AND chunk_relation.relname = chunks.chunk_name
    JOIN _timescaledb_catalog.chunk AS catalog_chunk
      ON catalog_chunk.relid = chunk_relation.oid
    CROSS JOIN c10_window AS target
    CROSS JOIN c10_window AS far
    WHERE target.window_kind = 'target'
      AND far.window_kind = 'far'
      AND chunks.hypertable_schema = 'ts'
      AND chunks.hypertable_name = 'telemetry_measurement'
), raw_relations AS (
    SELECT raw.window_kind,
           raw.logical_chunk_id,
           raw.logical_chunk,
           raw.schema_name,
           raw.table_name
    FROM raw_chunks AS raw
    UNION ALL
    SELECT raw.window_kind,
           raw.logical_chunk_id,
           raw.logical_chunk,
           compressed_namespace.nspname,
           compressed_relation.relname
    FROM raw_chunks AS raw
    JOIN pg_namespace AS logical_namespace
      ON logical_namespace.nspname = raw.schema_name
    JOIN pg_class AS logical_relation
      ON logical_relation.relnamespace = logical_namespace.oid
     AND logical_relation.relname = raw.table_name
    JOIN _timescaledb_catalog.compression_settings AS settings
      ON settings.relid = logical_relation.oid
    JOIN pg_class AS compressed_relation
      ON compressed_relation.oid = settings.compress_relid
    JOIN pg_namespace AS compressed_namespace
      ON compressed_namespace.oid = compressed_relation.relnamespace
)
INSERT INTO c10_chunk_relation (
    source_name,
    window_kind,
    logical_chunk_id,
    logical_chunk,
    schema_name,
    table_name)
SELECT 'raw',
       raw.window_kind,
       raw.logical_chunk_id,
       raw.logical_chunk,
       raw.schema_name,
       raw.table_name
FROM raw_relations AS raw;

WITH materialized_chunks AS (
    SELECT catalog_chunk.id AS logical_chunk_id,
           format('%I.%I', chunks.chunk_schema, chunks.chunk_name) AS logical_chunk,
           chunks.chunk_schema AS schema_name,
           chunks.chunk_name AS table_name,
           CASE
               WHEN chunks.range_end > target.start_at AND chunks.range_start < target.end_at
                   THEN 'target'
               WHEN chunks.range_end > far.start_at AND chunks.range_start < far.end_at
                   THEN 'far'
               ELSE 'outside'
           END AS window_kind
    FROM timescaledb_information.chunks AS chunks
    JOIN pg_namespace AS chunk_namespace
      ON chunk_namespace.nspname = chunks.chunk_schema
    JOIN pg_class AS chunk_relation
      ON chunk_relation.relnamespace = chunk_namespace.oid
     AND chunk_relation.relname = chunks.chunk_name
    JOIN _timescaledb_catalog.chunk AS catalog_chunk
      ON catalog_chunk.relid = chunk_relation.oid
    CROSS JOIN c10_materialization AS materialization
    CROSS JOIN c10_window AS target
    CROSS JOIN c10_window AS far
    WHERE target.window_kind = 'target'
      AND far.window_kind = 'far'
      AND chunks.hypertable_schema = materialization.schema_name
      AND chunks.hypertable_name = materialization.table_name
), materialized_relations AS (
    SELECT materialized.window_kind,
           materialized.logical_chunk_id,
           materialized.logical_chunk,
           materialized.schema_name,
           materialized.table_name
    FROM materialized_chunks AS materialized
    UNION ALL
    SELECT materialized.window_kind,
           materialized.logical_chunk_id,
           materialized.logical_chunk,
           compressed_namespace.nspname,
           compressed_relation.relname
    FROM materialized_chunks AS materialized
    JOIN pg_namespace AS logical_namespace
      ON logical_namespace.nspname = materialized.schema_name
    JOIN pg_class AS logical_relation
      ON logical_relation.relnamespace = logical_namespace.oid
     AND logical_relation.relname = materialized.table_name
    JOIN _timescaledb_catalog.compression_settings AS settings
      ON settings.relid = logical_relation.oid
    JOIN pg_class AS compressed_relation
      ON compressed_relation.oid = settings.compress_relid
    JOIN pg_namespace AS compressed_namespace
      ON compressed_namespace.oid = compressed_relation.relnamespace
)
INSERT INTO c10_chunk_relation (
    source_name,
    window_kind,
    logical_chunk_id,
    logical_chunk,
    schema_name,
    table_name)
SELECT 'rollup',
       materialized.window_kind,
       materialized.logical_chunk_id,
       materialized.logical_chunk,
       materialized.schema_name,
       materialized.table_name
FROM materialized_relations AS materialized;

SELECT pg_temp.c10_require(
    EXISTS (
        SELECT 1 FROM c10_chunk_relation
        WHERE source_name = 'raw' AND window_kind = 'far')
    AND EXISTS (
        SELECT 1 FROM c10_chunk_relation
        WHERE source_name = 'rollup' AND window_kind = 'far'),
    'C10 structural oracle needs both raw and materialized far-control chunks, but one is absent.');

CREATE TEMP TABLE c10_plan_physical_chunk AS
SELECT DISTINCT relations.scenario_name,
       chunks.source_name,
       chunks.window_kind,
       chunks.logical_chunk_id,
       chunks.logical_chunk,
       chunks.schema_name,
       chunks.table_name
FROM c10_plan_relation AS relations
JOIN c10_chunk_relation AS chunks
  ON chunks.schema_name = relations.schema_name
 AND chunks.table_name = relations.table_name;

CREATE TEMP TABLE c10_plan_oracle AS
SELECT scenario.ordinal,
       scenario.scenario_name,
       scenario.source_name,
       (
           SELECT count(*)
           FROM c10_plan_physical_chunk AS actual
           WHERE actual.scenario_name = scenario.scenario_name
             AND actual.source_name = scenario.source_name
             AND actual.window_kind = 'target'
       ) AS target_physical_relations,
       (
           SELECT count(DISTINCT actual.logical_chunk_id)
           FROM c10_plan_physical_chunk AS actual
           WHERE actual.scenario_name = scenario.scenario_name
             AND actual.source_name = scenario.source_name
             AND actual.window_kind = 'target'
       ) AS target_logical_chunks,
       (
           SELECT count(DISTINCT expected.logical_chunk_id)
           FROM c10_chunk_relation AS expected
           WHERE expected.source_name = scenario.source_name
             AND expected.window_kind = 'target'
       ) AS available_target_chunks,
       (
           SELECT count(DISTINCT expected.logical_chunk_id)
           FROM c10_chunk_relation AS expected
           WHERE expected.source_name = scenario.source_name
             AND expected.window_kind = 'target'
             AND NOT EXISTS (
                 SELECT 1
                 FROM c10_plan_physical_chunk AS actual
                 WHERE actual.scenario_name = scenario.scenario_name
                   AND actual.source_name = expected.source_name
                   AND actual.logical_chunk_id = expected.logical_chunk_id)
       ) AS missing_target_chunks,
       (
           SELECT count(DISTINCT actual.logical_chunk_id)
           FROM c10_plan_physical_chunk AS actual
           WHERE actual.scenario_name = scenario.scenario_name
             AND actual.source_name = scenario.source_name
             AND actual.window_kind <> 'target'
       ) AS excluded_window_chunks_read,
       (
           SELECT count(DISTINCT actual.logical_chunk_id)
           FROM c10_plan_physical_chunk AS actual
           WHERE actual.scenario_name = scenario.scenario_name
             AND actual.source_name <> scenario.source_name
       ) AS wrong_source_chunks,
       (
           SELECT string_agg(
               format('%I.%I', actual.schema_name, actual.table_name),
               ',' ORDER BY format('%I.%I', actual.schema_name, actual.table_name))
           FROM c10_plan_physical_chunk AS actual
           WHERE actual.scenario_name = scenario.scenario_name
             AND actual.source_name = scenario.source_name
             AND actual.window_kind = 'target'
       ) AS planned_target_relations,
       (
           SELECT string_agg(actual.logical_chunk, ',' ORDER BY actual.logical_chunk)
           FROM (
               SELECT DISTINCT physical.logical_chunk
               FROM c10_plan_physical_chunk AS physical
               WHERE physical.scenario_name = scenario.scenario_name
                 AND physical.source_name = scenario.source_name
                 AND physical.window_kind = 'target'
           ) AS actual
       ) AS planned_target_chunks
FROM c10_scenario AS scenario
-- Oracle cau truc nay chi bao duoc hai nguon ma no biet duong chunk: bang tho va rollup muc kenh.
-- `machine_rollup` (migration 013) nam tren mot materialization hypertable THU HAI, va day cho
-- oracle biet no can them mot khoi dung nhu khoi `c10_chunk_relation` o tren — khoang 50 dong gan
-- trung lap. Chua lam, va ghi ra day thay vi de nguoi doc tuong da lam.
--
-- Trong luc do, thu dung ra thay cho no KHONG phai la niem tin ma la hai so do khac, ca hai deu
-- manh hon mot phep dem chunk:
--   * doi chieu tung bucket voi duong muc kenh tren CA HAI cua so — 10.080 va 3.488 bucket,
--     lech 0 o sample_count va 0 o trung binh toi 9 chu so;
--   * `Buffers: shared hit=85` so voi 33.667 cua duong cu, tuc no khong the dang doc nham chunk.
WHERE scenario.source_name IN ('rollup', 'raw');

SELECT pg_temp.c10_require(
    (SELECT count(*) FROM c10_plan_oracle) = 4,
    'C10 structural oracle did not produce all four chunk-mapped scenarios.');

SELECT pg_temp.c10_require(
    oracle.target_physical_relations > 0
    AND oracle.target_logical_chunks > 0
    AND oracle.target_logical_chunks = oracle.available_target_chunks
    AND oracle.missing_target_chunks = 0
    AND (oracle.source_name <> 'raw' OR oracle.available_target_chunks = 7)
    AND (oracle.source_name <> 'rollup' OR oracle.available_target_chunks = 2)
    AND oracle.excluded_window_chunks_read = 0
    AND oracle.wrong_source_chunks = 0,
    format(
        'C10 %s plan oracle failed: target_physical=%s target_logical=%s available_target=%s missing_target=%s non_target_read=%s wrong_source=%s physical=%s logical=%s',
        oracle.scenario_name,
        oracle.target_physical_relations,
        oracle.target_logical_chunks,
        oracle.available_target_chunks,
        oracle.missing_target_chunks,
        oracle.excluded_window_chunks_read,
        oracle.wrong_source_chunks,
        coalesce(oracle.planned_target_relations, '<none>'),
        coalesce(oracle.planned_target_chunks, '<none>')))
FROM c10_plan_oracle AS oracle;

\pset format unaligned
\pset tuples_only on

SELECT format(
    'NVM_ROLLUP_FIXTURE site_id=NV1 start_at=2026-07-20T00:00:00Z end_at=2026-07-27T00:00:00Z sample_period_seconds=5 drifted_rate=0 temperature_rows=%s channels=%s machine_buckets=%s channel_0001_rows=%s channel_0001_buckets=%s first_sample_at=%s last_sample_at=%s good_rows=%s missing_channels=%s unexpected_channels=%s raw_chunks=7 raw_state=compressed far_control_start=2026-08-16T00:00:00Z far_control_end=2026-08-21T00:00:00Z',
    fingerprint.temperature_rows,
    fingerprint.channels,
    fingerprint.machine_buckets,
    fingerprint.channel_0001_rows,
    fingerprint.channel_0001_buckets,
    fingerprint.first_sample_at,
    fingerprint.last_sample_at,
    fingerprint.good_rows,
    fingerprint.missing_channels,
    fingerprint.unexpected_channels)
FROM c10_fixture_fingerprint AS fingerprint;

SELECT format(
    'NVM_ROLLUP_EQUIVALENCE scope=%s raw_buckets=%s rollup_buckets=%s raw_samples=%s rollup_samples=%s missing_buckets=%s extra_buckets=%s sample_mismatch_buckets=%s weighted_mismatch_buckets=%s raw_weighted_value=%s rollup_weighted_value=%s max_bucket_weighted_delta=%s total_weighted_delta=%s',
    equivalence.scope_name,
    equivalence.raw_buckets,
    equivalence.rollup_buckets,
    equivalence.raw_samples,
    equivalence.rollup_samples,
    equivalence.missing_in_rollup,
    equivalence.extra_in_rollup,
    equivalence.sample_mismatch_buckets,
    equivalence.weighted_mismatch_buckets,
    to_char(equivalence.raw_weighted_value, 'FM999999999999990.000000000'),
    to_char(equivalence.rollup_weighted_value, 'FM999999999999990.000000000'),
    to_char(equivalence.max_weighted_delta, 'FM999999999999990.000000000'),
    to_char(equivalence.weighted_delta, 'FM999999999999990.000000000'))
FROM c10_equivalence AS equivalence
ORDER BY equivalence.scope_name;

SELECT format(
    'NVM_ROLLUP_TRIAL run=%s position=%s scenario=%s source=%s scope=%s elapsed_ms=%s checksum=%s',
    timing.run_number,
    mod(scenario.ordinal - timing.run_number + 40, 4) + 1,
    timing.scenario_name,
    scenario.source_name,
    scenario.scope_name,
    to_char(timing.elapsed_ms, 'FM999999990.000'),
    timing.checksum)
FROM c10_timing AS timing
JOIN c10_scenario AS scenario USING (scenario_name)
WHERE NOT timing.warmup
ORDER BY timing.run_number, mod(scenario.ordinal - timing.run_number + 40, 4);

SELECT format(
    'NVM_ROLLUP_TIMING scenario=%s source=%s scope=%s warmup_runs=1 measured_runs=%s timing=server_clock percentile_method=disc_nearest_rank checksum=%s p50_ms=%s p95_ms=%s max_ms=%s gate=%s threshold_ms=%s',
    summary.scenario_name,
    summary.source_name,
    summary.scope_name,
    summary.measured_runs,
    summary.checksum,
    to_char(summary.p50_ms, 'FM999999990.000'),
    to_char(summary.p95_ms, 'FM999999990.000'),
    to_char(summary.max_ms, 'FM999999990.000'),
    CASE
        WHEN summary.enforce_gate AND summary.p95_ms < 200 THEN 'pass'
        WHEN summary.enforce_gate THEN 'fail'
        ELSE 'control_not_gated'
    END,
    CASE WHEN summary.enforce_gate THEN '200' ELSE 'none' END)
FROM c10_summary AS summary
ORDER BY summary.ordinal;

SELECT format(
    'NVM_ROLLUP_PLAN scenario=%s source=%s target_physical_relations=%s target_logical_chunks=%s available_target_chunks=%s missing_target_chunks=%s non_target_chunks_read=%s wrong_source_chunks=%s planned_target_relations=%s planned_target_chunks=%s oracle=pass',
    oracle.scenario_name,
    oracle.source_name,
    oracle.target_physical_relations,
    oracle.target_logical_chunks,
    oracle.available_target_chunks,
    oracle.missing_target_chunks,
    oracle.excluded_window_chunks_read,
    oracle.wrong_source_chunks,
    oracle.planned_target_relations,
    oracle.planned_target_chunks)
FROM c10_plan_oracle AS oracle
ORDER BY oracle.ordinal;

SELECT format(
    'NVM_ROLLUP_EXPLAIN scenario=%s explain=%s',
    plans.scenario_name,
    plans.plan::TEXT)
FROM c10_plan AS plans
JOIN c10_scenario AS scenario USING (scenario_name)
ORDER BY scenario.ordinal;

-- D2 is deliberately strict only on the two rollup scenarios. Raw queries are controls that
-- quantify why the continuous aggregate exists; a slow control is evidence, not a failed product.
SELECT pg_temp.c10_require(
    summary.p95_ms < 200,
    format(
        'C10 D2 failed for %s: p95 %s ms is not strictly below 200 ms',
        summary.scenario_name,
        to_char(summary.p95_ms, 'FM999999990.000')))
FROM c10_summary AS summary
WHERE summary.enforce_gate;

SELECT 'NVM_ROLLUP_SUMMARY gate_status=pass rollup_p95_threshold_ms=200 percentile_method=disc_nearest_rank comparison=raw_not_gated';

SELECT pg_advisory_unlock(hashtextextended('novavolt.c10.rollup-bench', 0));
