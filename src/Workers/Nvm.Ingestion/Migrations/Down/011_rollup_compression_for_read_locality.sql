-- Remove the job before changing the storage setting, otherwise a scheduler run can race rollback.
SELECT remove_compression_policy('ts.process_signal_1m', if_exists => true);

-- TimescaleDB cannot disable compression while compressed chunks remain, so rollback cost is
-- proportional to the compressed volume. Same shape as the Down of 004.
--
-- The materialization hypertable is resolved through the catalogue, never by its generated name.
-- The first version of this script hardcoded `_materialized_hypertable_4`, which is the number this
-- one machine happens to have; a fresh database numbers it differently and the rollback would have
-- silently decompressed nothing while reporting success.
DO $rollback$
DECLARE
    chunk_to_decompress REGCLASS;
BEGIN
    FOR chunk_to_decompress IN
        SELECT format('%I.%I', chunks.chunk_schema, chunks.chunk_name)::REGCLASS
        FROM timescaledb_information.chunks AS chunks
        JOIN timescaledb_information.continuous_aggregates AS aggregates
          ON aggregates.materialization_hypertable_schema = chunks.hypertable_schema
         AND aggregates.materialization_hypertable_name = chunks.hypertable_name
        WHERE aggregates.view_schema = 'ts'
          AND aggregates.view_name = 'process_signal_1m'
          AND chunks.is_compressed
    LOOP
        PERFORM decompress_chunk(chunk_to_decompress);
    END LOOP;
END
$rollback$;

ALTER MATERIALIZED VIEW ts.process_signal_1m SET (timescaledb.compress = false);
