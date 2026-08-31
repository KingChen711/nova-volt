\set ON_ERROR_STOP on

-- C15-3 — cung mot truy van cua D2, o muc LINE `F1` = 10 cycler x 100 kenh = 1.000 kenh.
--
-- Day la EVIDENCE, khong phai gate M3. D2 da dat o 144,061 ms tren `FORM-01` (mot cycler,
-- 100 kenh) va `ADR-034` chot ranh gioi do. Muc nay tra loi mot cau khac: truy van ma mot
-- ky su quy trinh se chay that o M6/M7 — "nhiet do trung binh moi phut cua CA LINE trong
-- 7 ngay" — mat bao lau. Neu p95 >= 200 ms thi ghi NO co deadline truoc dashboard
-- line-wide cua M6/M7, khong phai lam M3 truot.
--
-- Vi sao file rieng thay vi them scenario vao rollup-bench.sql: file do la gate D2 dang
-- xanh, co fingerprint da ghim va mot chuoi assertion ve chunk exclusion. Them mot fixture
-- 1.000 kenh vao giua se lam moi so cu phai do lai de chung minh chung khong doi. Hai file,
-- cung phuong phap do, hai fixture doc lap.
--
-- Phuong phap giu NGUYEN cua C10 de hai con so so duoc voi nhau:
--   - cung hinh dang truy van va cung cach lay checksum;
--   - 1 lan warmup roi 10 lan do, dan xen scenario de khong scenario nao lay het trang thai
--     cache som hoac muon;
--   - p95 = percentile_disc, tuc quan sat thu 10 (te nhat) tren 10 mau. Bao thu co y.
--
-- Fixture:
--   make telemetry-backfill CHANNELS=1000 DAYS=7 END_AT=2026-05-20T00:00:00Z \
--     SAMPLE_PERIOD_SECONDS=5 DRIFTED_RATE=0
--
-- `DRIFTED_RATE=0` la co y: dung dieu kien fixture C10. Lech dong ho khong doi so row nhung
-- no lam hai chunk bien khong phai ngay san xuat day du, va luc do hai con so khong con
-- cung dieu kien du lieu.
--
-- CUA SO LA 6 NGAY, KHONG PHAI 7 — va day la su that phai ke, khong phai chi tiet lam tron.
-- Luot sinh chay 6 gio roi chet o moc 68 (~68,8 trieu row) voi
-- `Exception while reading from stream`; log Postgres ghi `unexpected EOF on client connection
-- with an open transaction`, RestartCount=0, khong OOM — tuc CLIENT roi mang, server binh
-- thuong. Sau su co, sau ngay 2026-05-13..18 tron ven o du 1.000 kenh, ngay 05-19 do dang.
--
-- Chon do 6 ngay tron thay vi chay lai 6 gio nua: so line la EVIDENCE chu khong phai gate, va
-- cua so 6 ngay la mot fixture DAY DU va DONG NHAT. Bu lai, su co cho mot phep do TOT HON ke
-- hoach ban dau: cung fixture nay chua ca 100 kenh cua `FORM-01`, nen may va line do duoc tren
-- CUNG du lieu, CUNG cua so, chi khac cardinality. Ti so giua hai con so do moi la cau tra loi;
-- con so `FORM-01` o day con doi chung duoc voi 144,061 ms ma C10 do doc lap.

SET client_min_messages = warning;
SET lock_timeout = '30s';
SET statement_timeout = '60min';

-- Mot session mot luc duoc dong vao cua so nay.
SELECT pg_advisory_lock(hashtextextended('novavolt.c15.rollup-bench-line', 0));

CREATE FUNCTION pg_temp.c15_require(condition BOOLEAN, message TEXT)
RETURNS VOID
LANGUAGE plpgsql
AS $require$
BEGIN
    IF condition IS DISTINCT FROM true THEN
        RAISE EXCEPTION USING ERRCODE = 'P4090', MESSAGE = message;
    END IF;
END
$require$;

CREATE TEMP TABLE c15_window (
    start_at TIMESTAMPTZ PRIMARY KEY,
    end_at   TIMESTAMPTZ NOT NULL,
    line_prefix TEXT NOT NULL
) ON COMMIT PRESERVE ROWS;

INSERT INTO c15_window (start_at, end_at, line_prefix)
VALUES (
    TIMESTAMPTZ '2026-05-13 00:00:00Z',
    TIMESTAMPTZ '2026-05-19 00:00:00Z',
    'NOVAVOLT/NV1/FORMATION/F1/');

-- ─────────────────────────────────────────────────── fingerprint ──
--
-- So row duoc GHIM sau lan chay dau tien, dung cach C09/C10 da lam. Truoc khi ghim, dat
-- :expected_rows = 0 va script chi in fingerprint roi dung lai — no KHONG chay tiep voi
-- mot fixture chua biet hinh dang, vi mot benchmark tren fixture khong xac dinh la mot so
-- khong tai lap duoc.
\if :{?expected_rows}
\else
\set expected_rows 0
\endif

CREATE TEMP TABLE c15_fingerprint AS
SELECT count(*) AS temperature_rows,
       count(DISTINCT measurements.equipment_id) AS channels,
       count(DISTINCT split_part(measurements.equipment_id, '/', 5)) AS cyclers,
       count(DISTINCT time_bucket(INTERVAL '1 minute', measurements.device_timestamp)) AS line_buckets,
       count(*) FILTER (WHERE measurements.clock_quality = 'Good') AS good_rows,
       count(*) FILTER (WHERE measurements.clock_quality <> 'Good') AS non_good_rows,
       count(*) FILTER (
           WHERE measurements.equipment_id
               LIKE 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/%') AS machine_rows,
       count(DISTINCT measurements.equipment_id) FILTER (
           WHERE measurements.equipment_id
               LIKE 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/%') AS machine_channels,
       min(measurements.device_timestamp) AS first_sample_at,
       max(measurements.device_timestamp) AS last_sample_at,
       (SELECT count(*)
        FROM ts.telemetry_measurement AS other
        CROSS JOIN c15_window AS w2
        WHERE other.site_id IS DISTINCT FROM 'NV1'
          AND other.device_timestamp >= w2.start_at
          AND other.device_timestamp < w2.end_at) AS other_site_rows
FROM ts.telemetry_measurement AS measurements
CROSS JOIN c15_window AS w
WHERE measurements.site_id = 'NV1'
  AND measurements.equipment_id LIKE w.line_prefix || '%'
  AND measurements.signal_code = 'Formation/Temperature'
  AND measurements.value_kind = 'real'
  AND measurements.device_timestamp >= w.start_at
  AND measurements.device_timestamp < w.end_at;

SELECT format(
    'NVM_LINE_FIXTURE_FINGERPRINT temperature_rows=%s channels=%s cyclers=%s machine_rows=%s machine_channels=%s line_buckets=%s good=%s non_good=%s other_site=%s first=%s last=%s',
    fingerprint.temperature_rows,
    fingerprint.channels,
    fingerprint.cyclers,
    fingerprint.machine_rows,
    fingerprint.machine_channels,
    fingerprint.line_buckets,
    fingerprint.good_rows,
    fingerprint.non_good_rows,
    fingerprint.other_site_rows,
    to_char(fingerprint.first_sample_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    to_char(fingerprint.last_sample_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'))
FROM c15_fingerprint AS fingerprint;

-- Bat buoc dung ve HINH DANG, khong phu thuoc con so chua ghim: dung 1.000 kenh tren dung
-- 10 cycler, khong co dong ho lech (DRIFTED_RATE=0), khong co row cua site khac, va hai bien
-- dung khop cua so.
SELECT pg_temp.c15_require(
    fingerprint.channels = 1000
    AND fingerprint.cyclers = 10
    AND fingerprint.machine_channels = 100
    AND fingerprint.non_good_rows = 0
    AND fingerprint.other_site_rows = 0
    AND fingerprint.first_sample_at = (SELECT start_at FROM c15_window)
    AND fingerprint.last_sample_at = (SELECT end_at - INTERVAL '5 seconds' FROM c15_window),
    format(
        'C15-3 line fixture shape mismatch. Sai o: %s. Da thay: channels=%s cyclers=%s non_good=%s other_site=%s range=[%s, %s]. Mong doi: channels=1000 cyclers=10 non_good=0 other_site=0 range=[%s, %s]',
        array_to_string(
            array_remove(ARRAY[
                CASE WHEN fingerprint.channels <> 1000 THEN 'channels' END,
                CASE WHEN fingerprint.cyclers <> 10 THEN 'cyclers' END,
                CASE WHEN fingerprint.machine_channels <> 100 THEN 'machine_channels' END,
                CASE WHEN fingerprint.non_good_rows <> 0 THEN 'non_good_rows' END,
                CASE WHEN fingerprint.other_site_rows <> 0 THEN 'other_site_rows' END,
                CASE WHEN fingerprint.first_sample_at
                          IS DISTINCT FROM (SELECT start_at FROM c15_window)
                     THEN 'first_sample_at' END,
                CASE WHEN fingerprint.last_sample_at
                          IS DISTINCT FROM (SELECT end_at - INTERVAL '5 seconds' FROM c15_window)
                     THEN 'last_sample_at (fixture chua sinh xong?)' END
            ], NULL),
            ', '),
        fingerprint.channels,
        fingerprint.cyclers,
        fingerprint.non_good_rows,
        fingerprint.other_site_rows,
        fingerprint.first_sample_at,
        fingerprint.last_sample_at,
        (SELECT start_at FROM c15_window),
        (SELECT end_at - INTERVAL '5 seconds' FROM c15_window)))
FROM c15_fingerprint AS fingerprint;

SELECT pg_temp.c15_require(
    :expected_rows > 0,
    format(
        'C15-3 expected_rows chua duoc ghim. Chay lai voi: psql -v expected_rows=%s',
        (SELECT temperature_rows FROM c15_fingerprint)));

SELECT pg_temp.c15_require(
    fingerprint.temperature_rows = :expected_rows,
    format(
        'C15-3 line fixture row count drifted: found %s, pinned %s',
        fingerprint.temperature_rows,
        :expected_rows))
FROM c15_fingerprint AS fingerprint;

-- ──────────────────────────────────── trang thai vat ly on dinh ──
--
-- Muc tieu: moi chunk cua cua so o CUNG mot trang thai vat ly truoc khi do. Neu khong, lan do
-- dau cham mot rowstore vua COPY xong con bloat, lan sau cham columnstore — hai lan do hai thu.
--
-- CHI nen chunk NAO CHUA o trang thai `status = 1`. Ban dau vong lap nay goi
-- `compress_chunk(..., recompress => true)` VO DIEU KIEN, chep tu compression-report.sql sang.
-- O file do no dung, vi file do CO Y giai nen truoc de chuan hoa baseline rowstore. O day thi
-- sai: sau chunk deu da `status = 1` (nen day du, khong con duoi rowstore) do compression policy
-- lam trong luc backfill chay, va `recompress => true` van ep nen lai tu dau. Ket qua: 60 phut
-- lam lai viec da xong, roi chet vi statement_timeout.
--
-- Y nghia cua `status` (`_timescaledb_catalog.chunk`): 0 chua nen · 1 nen day du · 3 nen nhung
-- unordered · 9 nen nhung CON DUOI rowstore · 11 ca hai. Chi 1 la trang thai on dinh; moi gia
-- tri khac deu can nen lai that.
DO $compression$
DECLARE
    target RECORD;
BEGIN
    FOR target IN
        SELECT format('%I.%I', chunks.chunk_schema, chunks.chunk_name)::REGCLASS AS chunk,
               catalogue.status
        FROM timescaledb_information.chunks AS chunks
        CROSS JOIN c15_window AS w
        JOIN _timescaledb_catalog.chunk AS catalogue
          ON catalogue.relid = format('%I.%I', chunks.chunk_schema, chunks.chunk_name)::REGCLASS
        WHERE chunks.hypertable_schema = 'ts'
          AND chunks.hypertable_name = 'telemetry_measurement'
          AND chunks.range_start >= w.start_at
          AND chunks.range_end <= w.end_at
        ORDER BY chunks.range_start
    LOOP
        IF target.status <> 1 THEN
            PERFORM compress_chunk(target.chunk, if_not_compressed => true, recompress => true);
        END IF;
    END LOOP;
END
$compression$;

-- Khang dinh trang thai da dat, thay vi tin rang vong lap tren da lam dung. Neu mot chunk con
-- `status <> 1` thi moi con so thoi gian ben duoi do mot hinh dang vat ly khac.
SELECT pg_temp.c15_require(
    count(*) = 6 AND count(*) FILTER (WHERE catalogue.status = 1) = 6,
    format(
        'C15-3 cua so phai co dung 6 chunk deu o status=1; thay %s chunk, trong do %s o status=1',
        count(*),
        count(*) FILTER (WHERE catalogue.status = 1)))
FROM timescaledb_information.chunks AS chunks
CROSS JOIN c15_window AS w
JOIN _timescaledb_catalog.chunk AS catalogue
  ON catalogue.relid = format('%I.%I', chunks.chunk_schema, chunks.chunk_name)::REGCLASS
WHERE chunks.hypertable_schema = 'ts'
  AND chunks.hypertable_name = 'telemetry_measurement'
  AND chunks.range_start >= w.start_at
  AND chunks.range_end <= w.end_at;

-- ────────────────────────────── materialize continuous aggregate ──
--
-- Policy refresh co `start_offset = 5 gio` nen no KHONG BAO GIO voi toi thang 5. Day chinh la
-- hien tuong D5 do; khac o cho lan nay ta biet truoc va chu dong goi. Bo buoc nay thi rollup
-- rong va benchmark se do mot bang trong roi bao 2 ms.
CALL refresh_continuous_aggregate(
    'ts.process_signal_1m',
    TIMESTAMPTZ '2026-05-13 00:00:00Z',
    TIMESTAMPTZ '2026-05-19 00:00:00Z');

-- ───────────────────────────────── raw va rollup phai noi cung mot dieu ──
--
-- Mot benchmark nhanh tren mot rollup SAI la mot benchmark vo nghia. Doi chieu truoc khi do.
CREATE TEMP TABLE c15_equivalence AS
WITH w AS (SELECT * FROM c15_window),
raw_side AS (
    SELECT time_bucket(INTERVAL '1 minute', measurements.device_timestamp) AS bucket,
           count(*) AS samples,
           sum(measurements.real_value) AS value_sum
    FROM ts.telemetry_measurement AS measurements
    CROSS JOIN w
    WHERE measurements.site_id = 'NV1'
      AND measurements.equipment_id LIKE w.line_prefix || '%'
      AND measurements.signal_code = 'Formation/Temperature'
      AND measurements.value_kind = 'real'
      AND measurements.device_timestamp >= w.start_at
      AND measurements.device_timestamp < w.end_at
    GROUP BY bucket
),
rollup_side AS (
    SELECT rollup.bucket,
           sum(rollup.sample_count)::BIGINT AS samples,
           sum(rollup.avg_value * rollup.sample_count::DOUBLE PRECISION) AS value_sum
    FROM ts.process_signal_1m AS rollup
    CROSS JOIN w
    WHERE rollup.site_id = 'NV1'
      AND rollup.equipment_id LIKE w.line_prefix || '%'
      AND rollup.signal_code = 'Formation/Temperature'
      AND rollup.bucket >= w.start_at
      AND rollup.bucket < w.end_at
    GROUP BY rollup.bucket
)
SELECT count(*) FILTER (WHERE raw_side.bucket IS NULL) AS rollup_only_buckets,
       count(*) FILTER (WHERE rollup_side.bucket IS NULL) AS raw_only_buckets,
       count(*) FILTER (
           WHERE raw_side.bucket IS NOT NULL
             AND rollup_side.bucket IS NOT NULL
             AND raw_side.samples IS DISTINCT FROM rollup_side.samples) AS sample_mismatches,
       count(*) FILTER (
           WHERE raw_side.bucket IS NOT NULL
             AND rollup_side.bucket IS NOT NULL
             AND round(raw_side.value_sum::NUMERIC, 6)
                 IS DISTINCT FROM round(rollup_side.value_sum::NUMERIC, 6)) AS value_mismatches,
       coalesce(sum(coalesce(raw_side.samples, rollup_side.samples)), 0) AS total_samples,
       count(*) AS total_buckets
FROM raw_side
FULL JOIN rollup_side ON rollup_side.bucket = raw_side.bucket;

SELECT format(
    'NVM_LINE_EQUIVALENCE buckets=%s samples=%s rollup_only=%s raw_only=%s sample_mismatch=%s value_mismatch=%s',
    equivalence.total_buckets,
    equivalence.total_samples,
    equivalence.rollup_only_buckets,
    equivalence.raw_only_buckets,
    equivalence.sample_mismatches,
    equivalence.value_mismatches)
FROM c15_equivalence AS equivalence;

SELECT pg_temp.c15_require(
    equivalence.rollup_only_buckets = 0
    AND equivalence.raw_only_buckets = 0
    AND equivalence.sample_mismatches = 0
    AND equivalence.value_mismatches = 0
    AND equivalence.total_buckets > 0,
    format(
        'C15-3 rollup does not agree with raw at line scope: buckets=%s rollup_only=%s raw_only=%s sample_mismatch=%s value_mismatch=%s',
        equivalence.total_buckets,
        equivalence.rollup_only_buckets,
        equivalence.raw_only_buckets,
        equivalence.sample_mismatches,
        equivalence.value_mismatches))
FROM c15_equivalence AS equivalence;

-- ─────────────────────────────────────────────────────── do thoi gian ──

CREATE TEMP TABLE c15_scenario (
    ordinal       INTEGER PRIMARY KEY,
    scenario_name TEXT    NOT NULL UNIQUE,
    benchmark_sql TEXT    NOT NULL
);

INSERT INTO c15_scenario (ordinal, scenario_name, benchmark_sql)
VALUES
    -- Doi chung o CUNG fixture, CUNG cua so: chi khac cardinality 100 so voi 1.000. Con so nay
    -- con phai xap xi 144,061 ms ma C10 do doc lap tren fixture khac — neu no lech xa thi thu
    -- lech la MAY DO, khong phai cardinality, va ca phep do nay khong dung duoc.
    (1, 'rollup_machine', $query$
        WITH minute_temperature AS (
            SELECT rollup.bucket,
                   sum(rollup.sample_count)::BIGINT AS samples,
                   sum(rollup.avg_value * rollup.sample_count::DOUBLE PRECISION)
                       / sum(rollup.sample_count)::DOUBLE PRECISION AS avg_temperature
            FROM ts.process_signal_1m AS rollup
            WHERE rollup.site_id = 'NV1'
              AND rollup.equipment_id LIKE 'NOVAVOLT/NV1/FORMATION/F1/FORM-01/%'
              AND rollup.signal_code = 'Formation/Temperature'
              AND rollup.bucket >= TIMESTAMPTZ '2026-05-13 00:00:00Z'
              AND rollup.bucket < TIMESTAMPTZ '2026-05-19 00:00:00Z'
            GROUP BY rollup.bucket
        )
        SELECT md5(concat_ws(
            '|',
            count(*),
            coalesce(sum(minute.samples), 0),
            round(coalesce(sum(minute.avg_temperature * minute.samples), 0)::NUMERIC, 6),
            coalesce(sum(extract(epoch FROM minute.bucket)), 0)))
        FROM minute_temperature AS minute
        $query$),
    (2, 'rollup_line', $query$
        WITH minute_temperature AS (
            SELECT rollup.bucket,
                   sum(rollup.sample_count)::BIGINT AS samples,
                   sum(rollup.avg_value * rollup.sample_count::DOUBLE PRECISION)
                       / sum(rollup.sample_count)::DOUBLE PRECISION AS avg_temperature
            FROM ts.process_signal_1m AS rollup
            WHERE rollup.site_id = 'NV1'
              AND rollup.equipment_id LIKE 'NOVAVOLT/NV1/FORMATION/F1/%'
              AND rollup.signal_code = 'Formation/Temperature'
              AND rollup.bucket >= TIMESTAMPTZ '2026-05-13 00:00:00Z'
              AND rollup.bucket < TIMESTAMPTZ '2026-05-19 00:00:00Z'
            GROUP BY rollup.bucket
        )
        SELECT md5(concat_ws(
            '|',
            count(*),
            coalesce(sum(minute.samples), 0),
            round(coalesce(sum(minute.avg_temperature * minute.samples), 0)::NUMERIC, 6),
            coalesce(sum(extract(epoch FROM minute.bucket)), 0)))
        FROM minute_temperature AS minute
        $query$),
    (3, 'raw_line', $query$
        WITH minute_temperature AS (
            SELECT time_bucket(INTERVAL '1 minute', measurements.device_timestamp) AS bucket,
                   count(*) AS samples,
                   avg(measurements.real_value) AS avg_temperature
            FROM ts.telemetry_measurement AS measurements
            WHERE measurements.site_id = 'NV1'
              AND measurements.equipment_id LIKE 'NOVAVOLT/NV1/FORMATION/F1/%'
              AND measurements.signal_code = 'Formation/Temperature'
              AND measurements.value_kind = 'real'
              AND measurements.device_timestamp >= TIMESTAMPTZ '2026-05-13 00:00:00Z'
              AND measurements.device_timestamp < TIMESTAMPTZ '2026-05-19 00:00:00Z'
            GROUP BY bucket
        )
        SELECT md5(concat_ws(
            '|',
            count(*),
            coalesce(sum(minute.samples), 0),
            round(coalesce(sum(minute.avg_temperature * minute.samples), 0)::NUMERIC, 6),
            coalesce(sum(extract(epoch FROM minute.bucket)), 0)))
        FROM minute_temperature AS minute
        $query$);

CREATE TEMP TABLE c15_timing (
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
    FOR scenario IN SELECT * FROM c15_scenario ORDER BY ordinal
    LOOP
        started := clock_timestamp();
        EXECUTE scenario.benchmark_sql INTO STRICT observed_checksum;
        INSERT INTO c15_timing (scenario_name, run_number, warmup, elapsed_ms, checksum)
        VALUES (scenario.scenario_name, 0, true,
                extract(epoch FROM clock_timestamp() - started) * 1000, observed_checksum);
    END LOOP;

    FOR run IN 1..10
    LOOP
        -- Dao thu tu moi vong: khong scenario nao luon nhan trang thai cache cua vi tri dau.
        FOR scenario IN SELECT * FROM c15_scenario ORDER BY mod(ordinal - run + 30, 3)
        LOOP
            started := clock_timestamp();
            EXECUTE scenario.benchmark_sql INTO STRICT observed_checksum;
            INSERT INTO c15_timing (scenario_name, run_number, warmup, elapsed_ms, checksum)
            VALUES (scenario.scenario_name, run, false,
                    extract(epoch FROM clock_timestamp() - started) * 1000, observed_checksum);
        END LOOP;
    END LOOP;
END
$timing$;

-- Checksum phai giong nhau qua ca 11 lan chay, ke ca warmup. Mot benchmark ma ket qua doi
-- giua cac lan la mot benchmark dang do hai truy van khac nhau.
--
-- Lam tron 6 chu so thap phan, KHONG phai 9. Ly do la mot so do, khong phai mot phong doan:
-- ban dau dung 9 va invariant do voi `measured=10 distinct=2`. Chay lai truy van 12 lan cho
-- 8 lan `2967929.682969960` va 3 lan `2967929.682969950` — lech 1e-8 tren mot gia tri 2,97e6,
-- tuc 3,4e-15 tuong doi, dung MOT ULP cua double. EXPLAIN xac nhan nguyen nhan: `Gather /
-- Workers Planned: 2 / Parallel Append`, nen thu tu gop partial sum cua so thuc khong co dinh.
--
-- Sau chu so cho bien an toan 100 lan so voi nhieu do (1e-8), trong khi bat ky thay doi DU LIEU
-- that nao — thieu mot mau nhiet do — se doi tong khoang 800 don vi. Day khong phai noi long
-- phep kiem; day la dat nguong tren nhieu cua dau phay dong va duoi moi tin hieu that.
--
-- KHONG chuyen truy van sang NUMERIC de cho on dinh: nhu vay se do mot truy van khac voi truy
-- van ma mot ky su quy trinh thuc su chay.
SELECT pg_temp.c15_require(
    stable.measured_runs = 10
    AND stable.distinct_checksums = 1
    AND stable.warmup_checksum = stable.measured_checksum,
    format(
        'C15-3 %s timing/checksum invariant failed: measured=%s distinct=%s',
        stable.scenario_name,
        stable.measured_runs,
        stable.distinct_checksums))
FROM (
    SELECT timing.scenario_name,
           count(*) FILTER (WHERE NOT timing.warmup) AS measured_runs,
           count(DISTINCT timing.checksum) FILTER (WHERE NOT timing.warmup) AS distinct_checksums,
           min(timing.checksum) FILTER (WHERE timing.warmup) AS warmup_checksum,
           min(timing.checksum) FILTER (WHERE NOT timing.warmup) AS measured_checksum
    FROM c15_timing AS timing
    GROUP BY timing.scenario_name
) AS stable;

CREATE TEMP TABLE c15_summary AS
SELECT scenario.ordinal,
       scenario.scenario_name,
       -- Voi 10 mau, p95 nearest-rank la quan sat thu 10 — quan sat te nhat. Bao thu co y,
       -- va giong het cach C10 lay p95 nen hai con so so duoc.
       percentile_disc(0.50) WITHIN GROUP (ORDER BY timing.elapsed_ms) AS p50_ms,
       percentile_disc(0.95) WITHIN GROUP (ORDER BY timing.elapsed_ms) AS p95_ms,
       min(timing.elapsed_ms) AS min_ms,
       max(timing.elapsed_ms) AS max_ms,
       min(timing.checksum) AS checksum,
       count(*) AS measured_runs
FROM c15_scenario AS scenario
JOIN c15_timing AS timing
  ON timing.scenario_name = scenario.scenario_name AND NOT timing.warmup
GROUP BY scenario.ordinal, scenario.scenario_name;

SELECT format(
    'NVM_LINE_TIMING scenario=%s runs=%s p50_ms=%s p95_ms=%s min_ms=%s max_ms=%s checksum=%s',
    summary.scenario_name,
    summary.measured_runs,
    to_char(summary.p50_ms, 'FM999990.000'),
    to_char(summary.p95_ms, 'FM999990.000'),
    to_char(summary.min_ms, 'FM999990.000'),
    to_char(summary.max_ms, 'FM999990.000'),
    summary.checksum)
FROM c15_summary AS summary
ORDER BY summary.ordinal;

-- Ket luan. KHONG RAISE khi vuot 200 ms: muc nay la evidence, va lam M3 truot o day chinh la
-- sieu mot DoD sau khi da do — dieu `ADR-034` tu choi theo ca hai chieu.
SELECT format(
    -- window_days duoc TINH tu cua so, khong ghi cung. Ban dau no la hang so 7 trong khi cua so
    -- thuc te la 6 ngay — mot cau sai nam ngay trong output cua chinh phep do, va la dung loai
    -- menh de tu tin khong co gi dung sau ma docs/audit-playbook.md §1.2 noi phai kiem.
    'NVM_LINE_VERDICT scope=line_F1 channels=1000 window_days=%s rollup_p95_ms=%s threshold_ms=200 status=%s note=%s',
    (SELECT extract(day FROM end_at - start_at)::int FROM c15_window),
    to_char(rollup.p95_ms, 'FM999990.000'),
    CASE WHEN rollup.p95_ms < 200 THEN 'within_budget' ELSE 'DEBT' END,
    CASE WHEN rollup.p95_ms < 200
         THEN 'evidence_only_not_an_m3_gate'
         ELSE 'record_debt_with_deadline_before_the_m6_m7_line_wide_dashboard'
    END)
FROM c15_summary AS rollup
WHERE rollup.scenario_name = 'rollup_line';

SELECT format(
    'NVM_LINE_ROLLUP_VALUE rollup_p95_ms=%s raw_p95_ms=%s speedup=%s',
    to_char(rollup.p95_ms, 'FM999990.000'),
    to_char(raw.p95_ms, 'FM999990.000'),
    to_char(raw.p95_ms / nullif(rollup.p95_ms, 0), 'FM999990.00'))
FROM c15_summary AS rollup
CROSS JOIN c15_summary AS raw
WHERE rollup.scenario_name = 'rollup_line'
  AND raw.scenario_name = 'raw_line';

-- Cau tra loi that cua muc nay: 10x cardinality doi lay bao nhieu lan thoi gian, do tren CUNG
-- du lieu va CUNG cua so. Ti so nay la thu duy nhat khong bi nhieu boi may do hay ngay do.
SELECT format(
    'NVM_LINE_CARDINALITY_COST machine_100ch_p95_ms=%s line_1000ch_p95_ms=%s channel_multiple=10 time_multiple=%s',
    to_char(machine.p95_ms, 'FM999990.000'),
    to_char(line.p95_ms, 'FM999990.000'),
    to_char(line.p95_ms / nullif(machine.p95_ms, 0), 'FM999990.00'))
FROM c15_summary AS machine
CROSS JOIN c15_summary AS line
WHERE machine.scenario_name = 'rollup_machine'
  AND line.scenario_name = 'rollup_line';

SELECT pg_advisory_unlock(hashtextextended('novavolt.c15.rollup-bench-line', 0));
