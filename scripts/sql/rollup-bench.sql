-- ADR-037: D2 kiểm kết quả, SLO và chunk exclusion; không ghim layout vật lý của engine.
CREATE TEMP TABLE bench_case (
    id integer PRIMARY KEY, fixture text, scope text, source text, enforce_slo boolean, read_sql text
);
CREATE TEMP TABLE bench_value (
    case_id integer, bucket timestamptz, samples numeric, avg_value double precision,
    PRIMARY KEY (case_id, bucket)
);
CREATE TEMP TABLE bench_timing (case_id integer, trial integer, elapsed_ms double precision);
CREATE TEMP TABLE bench_plan (case_id integer PRIMARY KEY, document jsonb);

INSERT INTO bench_case
SELECT row_number() OVER (ORDER BY f.name, scope.name, s.name), f.name, scope.name, s.name,
       (SELECT mode FROM bench_settings) = 'gate'
           AND ((scope.name = 'channel' AND s.name = 'rollup')
             OR (scope.name = 'machine' AND s.name = 'machine_rollup')),
       format('SELECT %s AS bucket, %s AS samples, %s AS avg_value
               FROM ts_scoped.%I WHERE site_id = %L AND signal_code = %L
               AND %I >= %L::timestamptz AND %I < %L::timestamptz AND %s %s
               GROUP BY 1',
           CASE WHEN s.name = 'raw' THEN 'time_bucket(INTERVAL ''1 minute'', device_timestamp)' ELSE 'bucket' END,
           CASE WHEN s.name = 'raw' THEN 'count(*)' ELSE 'sum(sample_count)' END,
           CASE WHEN s.name = 'raw' THEN 'avg(real_value)' ELSE 'sum(avg_value * sample_count) / sum(sample_count)' END,
           s.view_name, f.site_id, 'Formation/Temperature',
           CASE WHEN s.name = 'raw' THEN 'device_timestamp' ELSE 'bucket' END, f.start_at,
           CASE WHEN s.name = 'raw' THEN 'device_timestamp' ELSE 'bucket' END, f.end_at,
           CASE scope.name
               WHEN 'channel' THEN format('equipment_id = %L', f.line_path || '/FORM-01/FORM-01-CH-0001')
               WHEN 'machine' THEN CASE WHEN s.name = 'machine_rollup'
                   THEN format('machine_id = %L', f.line_path || '/FORM-01')
                   ELSE format('equipment_id LIKE %L', f.line_path || '/FORM-01/%') END
               ELSE format('%I LIKE %L', CASE WHEN s.name = 'machine_rollup' THEN 'machine_id' ELSE 'equipment_id' END,
                           f.line_path || '/%') END,
           CASE WHEN s.name = 'raw' THEN 'AND value_kind = ''real''' ELSE '' END)
FROM bench_fixture f CROSS JOIN bench_source s
CROSS JOIN (VALUES ('channel'), ('machine'), ('line')) AS scope(name)
WHERE ((SELECT mode FROM bench_settings) = 'line') = (scope.name = 'line')
  AND NOT (scope.name = 'channel' AND s.name = 'machine_rollup');

-- DDL đứng trước transaction: PostgreSQL chỉ cho ghi vào temp table đã tồn tại trong READ ONLY.
GRANT SELECT, INSERT ON bench_settings, bench_fixture, bench_source, bench_chunk,
    bench_case, bench_value, bench_timing, bench_plan TO nvm_grafana;
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL ROLE nvm_grafana;
SELECT current_setting('transaction_read_only') AS read_only,
       current_setting('transaction_isolation') AS isolation,
       current_setting('work_mem') AS work_mem,
       (SELECT extversion FROM pg_extension WHERE extname = 'timescaledb') AS timescaledb;

DO $fingerprint$
DECLARE f record; actual record;
BEGIN
    FOR f IN SELECT * FROM bench_fixture ORDER BY name LOOP
        WITH expected AS (
            SELECT format('%s/FORM-%s/FORM-%s-CH-%s', f.line_path,
                          lpad(((n - 1) / 100 + 1)::text, 2, '0'),
                          lpad(((n - 1) / 100 + 1)::text, 2, '0'),
                          lpad(((n - 1) % 100 + 1)::text, 4, '0')) AS equipment_id
            FROM generate_series(1, f.expected_channels) n
        )
        SELECT count(*) AS rows, count(DISTINCT m.equipment_id) AS channels,
               bool_and(m.clock_quality = 'Good' AND e.equipment_id IS NOT NULL) AS valid,
               min(device_timestamp) AS first_at, max(device_timestamp) AS last_at
        INTO actual
        FROM ts_scoped.telemetry_measurement m LEFT JOIN expected e USING (equipment_id)
        WHERE m.site_id = f.site_id AND m.equipment_id LIKE f.line_path || '/%'
          AND m.signal_code = 'Formation/Temperature' AND m.value_kind = 'real'
          AND m.device_timestamp >= f.start_at AND m.device_timestamp < f.end_at;
        RAISE NOTICE 'FIXTURE %: %', f.name, row_to_json(actual);
        PERFORM pg_temp.bench_require(actual.rows = f.expected_rows
            AND actual.channels = f.expected_channels AND actual.valid
            AND actual.first_at = f.start_at AND actual.last_at = f.expected_last_at,
            format('Fixture %s drifted or is absent. See plan M3 preparation commands.', f.name));
    END LOOP;
END $fingerprint$;

DO $values$
DECLARE c record; baseline integer; comparison record;
BEGIN
    FOR c IN SELECT * FROM bench_case ORDER BY id LOOP
        EXECUTE format('INSERT INTO bench_value SELECT %s, bucket, samples, avg_value FROM (%s) q', c.id, c.read_sql);
        PERFORM pg_temp.bench_require(EXISTS (SELECT FROM bench_value WHERE case_id = c.id),
            format('Empty result for %s/%s/%s', c.fixture, c.scope, c.source));
    END LOOP;
    FOR c IN SELECT * FROM bench_case WHERE source <> 'raw' ORDER BY id LOOP
        SELECT id INTO STRICT baseline FROM bench_case
        WHERE fixture = c.fixture AND scope = c.scope AND source = 'raw';
        SELECT count(*) FILTER (WHERE a.bucket IS NULL OR b.bucket IS NULL) AS missing,
               count(*) FILTER (WHERE a.samples IS DISTINCT FROM b.samples) AS count_mismatch,
               count(*) FILTER (WHERE a.avg_value IS NULL OR b.avg_value IS NULL
                   OR abs(a.avg_value - b.avg_value) > greatest(1, abs(a.avg_value)) * 1e-12) AS mean_mismatch,
               max(abs(a.avg_value - b.avg_value)) AS max_mean_delta
        INTO comparison
        FROM (SELECT * FROM bench_value WHERE case_id = baseline) a
        FULL JOIN (SELECT * FROM bench_value WHERE case_id = c.id) b USING (bucket);
        RAISE NOTICE 'EQUIVALENCE %/%/%: %', c.fixture, c.scope, c.source, row_to_json(comparison);
        PERFORM pg_temp.bench_require(comparison.missing = 0 AND comparison.count_mismatch = 0
            AND comparison.mean_mismatch = 0, 'Per-bucket rollup equivalence failed. Refresh parent then child separately.');
    END LOOP;
END $values$;

-- Giữ fingerprint D2 cũ: đủ row tổng chưa chứng minh mật độ từng mức đọc không bị đổi.
SELECT pg_temp.bench_require(
    count(*) = CASE c.scope WHEN 'channel' THEN 965 ELSE 10080 END
    AND sum(v.samples) = CASE c.scope WHEN 'channel' THEN 1172 ELSE 121429 END,
    'Pinned seven-day D2 bucket/sample fingerprint drifted.')
FROM bench_case c JOIN bench_value v ON v.case_id = c.id
WHERE c.fixture = 'isolated' AND c.source = 'raw' GROUP BY c.scope;

DO $timing$
DECLARE c record; trial integer; started timestamptz; elapsed double precision; actual record; expected record;
BEGIN
    -- Một warmup + mười mẫu đan xen. Không tính EXPLAIN instrumentation vào thời gian truy vấn.
    FOR trial IN 0..10 LOOP
        FOR c IN SELECT * FROM bench_case ORDER BY mod(id + trial, (SELECT count(*)::integer FROM bench_case)) LOOP
            started := clock_timestamp();
            EXECUTE format('SELECT count(*) AS buckets, sum(samples) AS samples,
                                   sum(avg_value * samples) AS weighted FROM (%s) q', c.read_sql) INTO actual;
            elapsed := extract(epoch FROM clock_timestamp() - started) * 1000;
            SELECT count(*) AS buckets, sum(samples) AS samples, sum(avg_value * samples) AS weighted
            INTO expected FROM bench_value WHERE case_id = c.id;
            PERFORM pg_temp.bench_require(actual.buckets = expected.buckets AND actual.samples = expected.samples
                AND abs(actual.weighted - expected.weighted) <= greatest(1, abs(expected.weighted)) * 1e-12,
                'Timed query disagrees with the per-bucket snapshot.');
            IF trial > 0 THEN INSERT INTO bench_timing VALUES (c.id, trial, elapsed); END IF;
        END LOOP;
    END LOOP;
END $timing$;

DO $explain$
DECLARE c record; plan jsonb; matching integer; unexpected integer; outside integer;
BEGIN
    FOR c IN SELECT scenarios.*, f.start_at, f.end_at FROM bench_case scenarios
             JOIN bench_fixture f ON f.name = scenarios.fixture ORDER BY scenarios.id LOOP
        EXECUTE format('EXPLAIN (ANALYZE, BUFFERS, VERBOSE, FORMAT JSON)
            SELECT count(*) AS buckets, sum(samples) AS samples,
                   sum(avg_value * samples) AS weighted FROM (%s) q', c.read_sql) INTO plan;
        INSERT INTO bench_plan VALUES (c.id, plan);
        -- ColumnarScan expose logical chunk qua Schema/Relation Name; không cần ánh xạ chunk vật lý con.
        WITH RECURSIVE nodes(node) AS (
            SELECT plan->0->'Plan'
            UNION ALL
            SELECT child FROM nodes CROSS JOIN LATERAL jsonb_array_elements(nodes.node->'Plans') child
        ), executed AS (
            SELECT DISTINCT chunks.* FROM nodes
            JOIN bench_chunk chunks ON chunks.chunk_schema = node->>'Schema'
                AND chunks.chunk_name = node->>'Relation Name'
            WHERE (node->>'Actual Loops')::numeric > 0
        )
        SELECT count(*) FILTER (WHERE source_name = c.source AND range_start < c.end_at AND range_end > c.start_at),
               count(*) FILTER (WHERE source_name <> c.source OR range_start >= c.end_at OR range_end <= c.start_at)
        INTO matching, unexpected FROM executed;
        SELECT count(*) INTO outside FROM bench_chunk WHERE source_name = c.source
            AND (range_start >= c.end_at OR range_end <= c.start_at);
        RAISE NOTICE 'CHUNKS %/%/%: executed_in_window=%, unexpected=%, outside_control=%',
            c.fixture, c.scope, c.source, matching, unexpected, outside;
        PERFORM pg_temp.bench_require(matching > 0 AND unexpected = 0 AND outside > 0,
            format('Chunk exclusion/source proof failed for %s/%s/%s; inspect saved EXPLAIN. Logical chunk identity and an outside control are required.',
                   c.fixture, c.scope, c.source));
    END LOOP;
END $explain$;

SELECT c.fixture, c.scope, c.source, c.enforce_slo, count(*) AS runs,
       round(percentile_disc(0.50) WITHIN GROUP (ORDER BY t.elapsed_ms)::numeric, 3) AS p50_ms,
       round(percentile_disc(0.95) WITHIN GROUP (ORDER BY t.elapsed_ms)::numeric, 3) AS p95_ms
FROM bench_case c JOIN bench_timing t ON t.case_id = c.id
GROUP BY c.id ORDER BY c.id;
SELECT f.name AS fixture, c.source_name, count(*) AS chunks,
       count(*) FILTER (WHERE c.is_compressed) AS compressed_chunks
FROM bench_fixture f JOIN bench_chunk c ON c.range_start < f.end_at AND c.range_end > f.start_at
GROUP BY f.name, c.source_name ORDER BY 1, 2;
SELECT c.fixture, c.scope, c.source, jsonb_pretty(p.document) AS explain
FROM bench_case c JOIN bench_plan p ON p.case_id = c.id ORDER BY c.id;

-- percentile_disc(0.95) với mười mẫu chính là mẫu lớn nhất; giữ gate bảo thủ hiện hành.
SELECT pg_temp.bench_require(count(*) = 10 AND max(t.elapsed_ms) < 200,
    format('D2 p95 must be < 200 ms: %s/%s/%s, observed %s ms', c.fixture, c.scope, c.source, max(t.elapsed_ms)))
FROM bench_case c JOIN bench_timing t ON t.case_id = c.id
WHERE c.enforce_slo GROUP BY c.id;
COMMIT;
