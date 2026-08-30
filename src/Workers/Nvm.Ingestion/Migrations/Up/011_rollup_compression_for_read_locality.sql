-- C07 built the one-minute rollup and left it as the only uncompressed, unclustered relation in
-- the read path. That was invisible while the database was small and every page stayed in cache.
-- It stopped being invisible on 2026-09-01, when D2 was re-measured on a database that had grown
-- from 5,70 to about 28 GB: p95 went 144,061 -> 201,547 ms and the gate failed.
--
-- The measurement, not the guess (EXPLAIN ANALYZE, BUFFERS on the C10 machine query):
--
--   Bitmap Index Scan returns   99.587 rows   <- exactly the rows wanted, index is doing its job
--   Filter removes                   0 rows   <- the predicate is not the problem
--   Heap Blocks: exact          33.400 blocks <- about THREE useful rows per 8 kB page
--
-- A materialized hypertable stores rows in refresh order, which interleaves every channel and
-- every signal. Reading one machine's temperature series therefore touches nearly every page of
-- the chunk. The raw hypertable already avoids this: 004 segments it by
-- (site_id, equipment_id, signal_code) so one segment is one physical curve. The rollup never got
-- the same treatment.
--
-- Same segmentby here, for the same reason and with the same trade-off already accepted at 004.
-- ADR-011 records the measured evidence behind that choice: cardinality moves the compression
-- ratio 0,245687 pp while a 4x change in row count moves it 0,004303 pp, so what matters is that a
-- segment holds one pure series.
--
-- Deliberately NOT a second-level rollup grouped by machine, which is what plan M3 section C10
-- prescribed for "if the machine level misses 200 ms". That prescription was written before the
-- cause was known, and it answers a different cause - too much aggregation - which the numbers
-- above rule out. AGENTS.md section 2.2 allows the simpler route to the same DoD; if this does not
-- bring D2 under budget, the second-level rollup is still the next step.
ALTER MATERIALIZED VIEW ts.process_signal_1m SET (
    timescaledb.compress = true,
    timescaledb.compress_segmentby = 'site_id, equipment_id, signal_code',
    timescaledb.compress_orderby = 'bucket DESC'
);

-- Seven days matches the raw table. The rollup is the read product, so the newest week - the part
-- an operator refreshes most - stays rowstore where single-bucket writes are cheapest, and
-- everything older becomes the compact form that D2 reads.
--
-- This is a COMPRESSION policy, not a retention policy. Migrations 007 and 010 removed both
-- retention jobs until legal hold exists in M12, and nothing here brings a delete back: after this
-- migration the count of policy_retention jobs in the whole schema must still be zero.
SELECT add_compression_policy(
    'ts.process_signal_1m',
    compress_after => INTERVAL '7 days');
