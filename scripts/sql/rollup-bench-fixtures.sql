\set ON_ERROR_STOP on
SET TIME ZONE 'UTC';
SET lock_timeout = '30s';
SET statement_timeout = '10min';

CREATE FUNCTION pg_temp.bench_require(condition boolean, message text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF condition IS DISTINCT FROM true THEN
        RAISE EXCEPTION USING MESSAGE = message;
    END IF;
END $$;

CREATE TEMP TABLE bench_settings (mode text CHECK (mode IN ('gate', 'line', 'prepare')));
INSERT INTO bench_settings VALUES (:'benchmark_mode');
CREATE TEMP TABLE bench_fixture (
    name text PRIMARY KEY, site_id text, line_path text, start_at timestamptz, end_at timestamptz,
    expected_channels integer, expected_rows bigint, expected_last_at timestamptz
);
-- Cửa sổ line chỉ có sáu ngày hoàn chỉnh; không trình bày nó như gate bảy ngày (ADR-034).
INSERT INTO bench_fixture VALUES
    ('isolated', 'NV1', 'NOVAVOLT/NV1/FORMATION/F1', '2026-07-20 00:00:00Z',
     '2026-07-27 00:00:00Z', 100, 121429, '2026-07-26 23:59:50Z'),
    ('line', 'NV1', 'NOVAVOLT/NV1/FORMATION/F1', '2026-05-13 00:00:00Z',
     '2026-05-19 00:00:00Z', 1000, 1040949, '2026-05-18 23:59:55Z');
DELETE FROM bench_fixture WHERE name = 'isolated' AND (SELECT mode FROM bench_settings) = 'line';

-- Chỉ dùng metadata công khai. Tên materialization/chunk là dữ liệu, không phải hằng số.
CREATE TEMP TABLE bench_source AS
SELECT 'raw'::text AS name, 'telemetry_measurement'::text AS view_name,
       'ts'::text AS schema_name, 'telemetry_measurement'::text AS table_name
UNION ALL
SELECT CASE view_name WHEN 'process_signal_1m' THEN 'rollup' ELSE 'machine_rollup' END,
       view_name, materialization_hypertable_schema, materialization_hypertable_name
FROM timescaledb_information.continuous_aggregates
WHERE view_schema = 'ts' AND view_name IN ('process_signal_1m', 'process_signal_machine_1m')
  AND materialized_only;
SELECT pg_temp.bench_require((SELECT count(*) FROM bench_source) = 3,
    'Both materialized-only rollups are required. Run make ingestion-migrate separately.');
CREATE TEMP TABLE bench_chunk AS
SELECT s.name AS source_name, c.chunk_schema, c.chunk_name, c.range_start, c.range_end,
       c.is_compressed
FROM timescaledb_information.chunks c
JOIN bench_source s ON (c.hypertable_schema, c.hypertable_name) = (s.schema_name, s.table_name);
