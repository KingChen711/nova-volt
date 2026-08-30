-- Remove the jobs before changing the storage setting, otherwise a scheduler run can race rollback.
SELECT remove_retention_policy('ts.telemetry_measurement', if_exists => true);
SELECT remove_compression_policy('ts.telemetry_measurement', if_exists => true);

-- TimescaleDB cannot disable compression while compressed chunks remain. Rollback is therefore
-- proportional to the compressed data volume and must be reviewed before production execution.
DO $rollback$
DECLARE
    chunk_to_decompress REGCLASS;
BEGIN
    FOR chunk_to_decompress IN
        SELECT format('%I.%I', chunks.chunk_schema, chunks.chunk_name)::REGCLASS
        FROM timescaledb_information.chunks AS chunks
        WHERE chunks.hypertable_schema = 'ts'
          AND chunks.hypertable_name = 'telemetry_measurement'
          AND chunks.is_compressed
    LOOP
        PERFORM decompress_chunk(chunk_to_decompress);
    END LOOP;
END
$rollback$;

ALTER TABLE ts.telemetry_measurement SET (timescaledb.compress = false);
