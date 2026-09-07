-- Chuẩn bị có ghi, tách khỏi phép đo chỉ đọc (ADR-037). Không sinh lại fixture.
SELECT pg_temp.bench_require(bool_and(start_at >= clock_timestamp() - interval '399 days'
    AND end_at < date_trunc('minute', clock_timestamp()) - interval '2 minutes'),
    'Fixture must be closed and inside the supported 399-day repair window.')
FROM bench_fixture;

-- Refresh parent trước child để child không tổng hợp bản parent cũ (D5).
SELECT format('CALL refresh_continuous_aggregate(%L, %L::timestamptz, %L::timestamptz);',
              'ts.' || s.view_name, f.start_at, f.end_at)
FROM bench_fixture f CROSS JOIN bench_source s
WHERE s.name <> 'raw'
ORDER BY f.start_at, CASE s.name WHEN 'rollup' THEN 0 ELSE 1 END
\gexec

-- Không ép recompress chunk đã nén; preparation không hứa xoá mọi hot tail hay chuẩn hoá cache.
SELECT DISTINCT format('SELECT compress_chunk(%L::regclass, if_not_compressed => true);',
                       format('%I.%I', c.chunk_schema, c.chunk_name))
FROM timescaledb_information.chunks c
JOIN bench_source s ON (c.hypertable_schema, c.hypertable_name) = (s.schema_name, s.table_name)
JOIN bench_fixture f ON c.range_start < f.end_at AND c.range_end > f.start_at
WHERE NOT c.is_compressed
\gexec
