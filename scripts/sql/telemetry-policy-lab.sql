\pset pager off
\pset format unaligned
\pset tuples_only on

-- Keep all ten comparison chunks invisible to background policies until every assertion has passed.
-- A failed psql session rolls the whole lab back instead of leaving a half-labelled fixture.
BEGIN;

CREATE TEMP TABLE c06_measurements (
    metric TEXT    NOT NULL,
    trial  INTEGER NOT NULL,
    value  NUMERIC NOT NULL,
    PRIMARY KEY (metric, trial)
);

CREATE TEMP TABLE c06_retention_probe (
    source_event_id UUID PRIMARY KEY
);

CREATE TEMP TABLE c06_compression_checks (
    trial                    INTEGER PRIMARY KEY,
    rows_before_late_write   BIGINT  NOT NULL,
    before_total_bytes       BIGINT  NOT NULL,
    after_total_bytes        BIGINT  NOT NULL,
    compression_status       TEXT    NOT NULL
);

CREATE TEMP TABLE c06_trial_chunks (
    trial           INTEGER     PRIMARY KEY,
    rowstore_time   TIMESTAMPTZ NOT NULL,
    compressed_time TIMESTAMPTZ NOT NULL
);

CREATE OR REPLACE FUNCTION pg_temp.insert_lab_rows(
    base_time TIMESTAMPTZ,
    row_count INTEGER,
    phase TEXT)
RETURNS NUMERIC
LANGUAGE plpgsql
AS $function$
DECLARE
    started_at TIMESTAMPTZ;
BEGIN
    started_at := clock_timestamp();

    WITH generated AS (
        SELECT gen_random_uuid() AS source_event_id,
               sample_number,
               base_time + sample_number * INTERVAL '1 millisecond' AS device_timestamp
        FROM generate_series(1, row_count) AS samples(sample_number)
    ), claimed AS (
        INSERT INTO ingest.processed_message (
            source_event_id,
            site_id,
            natural_key,
            first_seen_at)
        SELECT source_event_id,
               'NV1',
               'c06/' || phase || '/' || source_event_id::TEXT,
               clock_timestamp()
        FROM generated
        RETURNING source_event_id
    )
    INSERT INTO ts.telemetry_measurement (
        source_event_id,
        site_id,
        equipment_id,
        unit_id,
        step_code,
        signal_code,
        device_timestamp,
        gateway_timestamp,
        recorded_at,
        value_kind,
        real_value,
        integer_value,
        boolean_value,
        text_value,
        clock_quality)
    SELECT generated.source_event_id,
           'NV1',
           'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-C06',
           NULL,
           'C06-LAB',
           'Formation/Voltage',
           generated.device_timestamp,
           generated.device_timestamp,
           clock_timestamp(),
           'real',
           3.5 + (sample_number % 500) / 1000.0,
           NULL,
           NULL,
           NULL,
           'Good'
    FROM generated
    JOIN claimed USING (source_event_id);

    RETURN extract(epoch FROM clock_timestamp() - started_at) * 1000;
END
$function$;

CREATE OR REPLACE FUNCTION pg_temp.prepare_compressed_trial(
    compressed_time TIMESTAMPTZ,
    current_trial INTEGER)
RETURNS VOID
LANGUAGE plpgsql
AS $function$
DECLARE
    treatment_chunk REGCLASS;
BEGIN
    SELECT format('%I.%I', chunks.chunk_schema, chunks.chunk_name)::REGCLASS
    INTO STRICT treatment_chunk
    FROM timescaledb_information.chunks AS chunks
    WHERE chunks.hypertable_schema = 'ts'
      AND chunks.hypertable_name = 'telemetry_measurement'
      AND chunks.range_start <= compressed_time
      AND chunks.range_end > compressed_time;

    -- Compression is deliberately outside the timed insert. Every treatment trial has its own
    -- populated chunk, so no trial measures an uncompressed delta left by an earlier trial.
    PERFORM compress_chunk(treatment_chunk, if_not_compressed => true, recompress => true);

    INSERT INTO c06_compression_checks (
        trial,
        rows_before_late_write,
        before_total_bytes,
        after_total_bytes,
        compression_status)
    SELECT current_trial,
           (
               SELECT count(*)
               FROM ts.telemetry_measurement
               WHERE device_timestamp >= date_trunc('day', compressed_time)
                 AND device_timestamp < date_trunc('day', compressed_time) + INTERVAL '1 day'),
           stats.before_compression_total_bytes,
           stats.after_compression_total_bytes,
           stats.compression_status
    FROM chunk_compression_stats('ts.telemetry_measurement') AS stats
    WHERE format('%I.%I', stats.chunk_schema, stats.chunk_name)::REGCLASS = treatment_chunk;
END
$function$;

CREATE OR REPLACE FUNCTION pg_temp.measure_lab()
RETURNS VOID
LANGUAGE plpgsql
AS $function$
DECLARE
    trial_chunk RECORD;
BEGIN
    FOR trial_chunk IN
        SELECT trial, rowstore_time, compressed_time
        FROM c06_trial_chunks
        ORDER BY trial
    LOOP
        -- Both sides receive the same 50.001-row baseline before the treatment is compressed.
        PERFORM pg_temp.insert_lab_rows(
            trial_chunk.rowstore_time,
            1,
            'rowstore-seed-' || trial_chunk.trial);
        PERFORM pg_temp.insert_lab_rows(
            trial_chunk.compressed_time,
            1,
            'compressed-seed-' || trial_chunk.trial);
        PERFORM pg_temp.insert_lab_rows(
            trial_chunk.rowstore_time + INTERVAL '30 minutes',
            50000,
            'rowstore-baseline-' || trial_chunk.trial);
        PERFORM pg_temp.insert_lab_rows(
            trial_chunk.compressed_time + INTERVAL '30 minutes',
            50000,
            'compressed-baseline-' || trial_chunk.trial);

        PERFORM pg_temp.prepare_compressed_trial(trial_chunk.compressed_time, trial_chunk.trial);

        IF trial_chunk.trial % 2 = 0 THEN

            INSERT INTO c06_measurements (metric, trial, value)
            VALUES (
                'compressed_insert_ms',
                trial_chunk.trial,
                pg_temp.insert_lab_rows(
                    trial_chunk.compressed_time + INTERVAL '2 hours',
                    10000,
                    'compressed-' || trial_chunk.trial));

            INSERT INTO c06_measurements (metric, trial, value)
            VALUES (
                'uncompressed_insert_ms',
                trial_chunk.trial,
                pg_temp.insert_lab_rows(
                    trial_chunk.rowstore_time + INTERVAL '2 hours',
                    10000,
                    'rowstore-' || trial_chunk.trial));
        ELSE
            INSERT INTO c06_measurements (metric, trial, value)
            VALUES (
                'uncompressed_insert_ms',
                trial_chunk.trial,
                pg_temp.insert_lab_rows(
                    trial_chunk.rowstore_time + INTERVAL '2 hours',
                    10000,
                    'rowstore-' || trial_chunk.trial));

            INSERT INTO c06_measurements (metric, trial, value)
            VALUES (
                'compressed_insert_ms',
                trial_chunk.trial,
                pg_temp.insert_lab_rows(
                    trial_chunk.compressed_time + INTERVAL '2 hours',
                    10000,
                    'compressed-' || trial_chunk.trial));
        END IF;
    END LOOP;
END
$function$;

-- Pick ten old but unexpired days with no chunks: five independent rowstore/compressed pairs.
WITH candidates AS (
    SELECT date_trunc('day', now()) - age_days * INTERVAL '1 day' AS range_start
    FROM generate_series(30, 300) AS ages(age_days)
), available AS (
    SELECT candidates.range_start,
           row_number() OVER (ORDER BY candidates.range_start DESC) AS candidate_number
    FROM candidates
    WHERE NOT EXISTS (
        SELECT 1
        FROM timescaledb_information.chunks AS chunks
        WHERE chunks.hypertable_schema = 'ts'
          AND chunks.hypertable_name = 'telemetry_measurement'
          AND chunks.range_start < candidates.range_start + INTERVAL '1 day'
          AND chunks.range_end > candidates.range_start)
)
INSERT INTO c06_trial_chunks (trial, rowstore_time, compressed_time)
SELECT ((candidate_number + 1) / 2)::INTEGER AS trial,
       max(range_start + INTERVAL '12 hours') FILTER (WHERE candidate_number % 2 = 1),
       max(range_start + INTERVAL '12 hours') FILTER (WHERE candidate_number % 2 = 0)
FROM available
WHERE candidate_number <= 10
GROUP BY ((candidate_number + 1) / 2)::INTEGER;

SELECT (count(*) = 5 AND bool_and(rowstore_time IS NOT NULL AND compressed_time IS NOT NULL))::TEXT
       AS c06_has_five_trial_pairs
FROM c06_trial_chunks
\gset

\if :c06_has_five_trial_pairs
\else
    ROLLBACK;
    \echo 'FAIL: fewer than ten empty, unexpired chunks are available for the paired measurement.'
    \quit 3
\endif

-- Alternate which side goes first across pairs so short-lived machine load does not always favour
-- one storage state. Baseline generation and compression are outside every timed insert.
SELECT pg_temp.measure_lab();

SELECT count(*) FILTER (WHERE rowstore_rows = 60001) AS c06_valid_rowstore_chunks,
       count(*) FILTER (WHERE compressed_rows = 60001) AS c06_valid_compressed_chunks
FROM (
    SELECT trial,
           (
               SELECT count(*)
               FROM ts.telemetry_measurement
               WHERE device_timestamp >= date_trunc('day', rowstore_time)
                 AND device_timestamp < date_trunc('day', rowstore_time) + INTERVAL '1 day') AS rowstore_rows,
           (
               SELECT count(*)
               FROM ts.telemetry_measurement
               WHERE device_timestamp >= date_trunc('day', compressed_time)
                 AND device_timestamp < date_trunc('day', compressed_time) + INTERVAL '1 day') AS compressed_rows
    FROM c06_trial_chunks) AS row_counts
\gset

SELECT count(*) FILTER (WHERE NOT chunks.is_compressed) AS c06_rowstore_chunks_uncompressed,
       count(*) FILTER (WHERE chunks.is_compressed) AS c06_compressed_chunks_compressed
FROM c06_trial_chunks AS trials
JOIN timescaledb_information.chunks AS chunks
  ON chunks.hypertable_schema = 'ts'
 AND chunks.hypertable_name = 'telemetry_measurement'
 AND (
        (chunks.range_start <= trials.rowstore_time AND chunks.range_end > trials.rowstore_time)
     OR (chunks.range_start <= trials.compressed_time AND chunks.range_end > trials.compressed_time))
WHERE (NOT chunks.is_compressed AND chunks.range_start <= trials.rowstore_time AND chunks.range_end > trials.rowstore_time)
   OR (chunks.is_compressed AND chunks.range_start <= trials.compressed_time AND chunks.range_end > trials.compressed_time)
\gset

SELECT percentile_disc(0.5) WITHIN GROUP (ORDER BY value) AS c06_rowstore_median_ms
FROM c06_measurements
WHERE metric = 'uncompressed_insert_ms'
\gset

SELECT percentile_disc(0.5) WITHIN GROUP (ORDER BY value) AS c06_compressed_median_ms
FROM c06_measurements
WHERE metric = 'compressed_insert_ms'
\gset

SELECT count(*) AS c06_timing_sample_count,
       count(*) FILTER (WHERE metric = 'uncompressed_insert_ms') AS c06_rowstore_sample_count,
       count(*) FILTER (WHERE metric = 'compressed_insert_ms') AS c06_compressed_sample_count,
       min(value) AS c06_min_timing_ms
FROM c06_measurements
\gset

SELECT percentile_disc(0.5) WITHIN GROUP (
           ORDER BY compressed.value / nullif(rowstore.value, 0)) AS c06_paired_median_ratio
FROM c06_measurements AS rowstore
JOIN c06_measurements AS compressed USING (trial)
WHERE rowstore.metric = 'uncompressed_insert_ms'
  AND compressed.metric = 'compressed_insert_ms'
\gset

SELECT count(*) AS c06_compression_check_count,
       min(rows_before_late_write) AS c06_min_compressed_rows,
       max(rows_before_late_write) AS c06_max_compressed_rows,
       min(before_total_bytes) AS c06_min_before_compression_bytes,
       bool_and(compression_status = 'Compressed')::TEXT AS c06_all_trials_compressed
FROM c06_compression_checks
\gset

-- The destructive sentinel must be the only row in its expired chunk. SHARE blocks concurrent
-- INSERT writers for this short final section, closing the check-then-act race before retention.
LOCK TABLE ts.telemetry_measurement IN SHARE MODE;

SELECT now() - INTERVAL '500 days' AS c06_expired_time
\gset

SELECT (count(*) = 0)::TEXT AS c06_expired_chunk_isolated
FROM timescaledb_information.chunks AS chunks
WHERE chunks.hypertable_schema = 'ts'
  AND chunks.hypertable_name = 'telemetry_measurement'
  AND chunks.range_start <= :'c06_expired_time'::TIMESTAMPTZ
  AND chunks.range_end > :'c06_expired_time'::TIMESTAMPTZ
\gset

\if :c06_expired_chunk_isolated
\else
    ROLLBACK;
    \echo 'FAIL: the 500-day retention sentinel would share an existing chunk.'
    \quit 3
\endif

-- The device clock is 500 days behind while the gateway and database clocks are current.
WITH probe AS (
    SELECT gen_random_uuid() AS source_event_id
), claim AS (
    INSERT INTO ingest.processed_message (
        source_event_id,
        site_id,
        natural_key,
        first_seen_at)
    SELECT source_event_id,
           'NV1',
           'c06/retention/' || source_event_id::TEXT,
           now()
    FROM probe
    RETURNING source_event_id
), telemetry AS (
    INSERT INTO ts.telemetry_measurement (
        source_event_id,
        site_id,
        equipment_id,
        unit_id,
        step_code,
        signal_code,
        device_timestamp,
        gateway_timestamp,
        recorded_at,
        value_kind,
        real_value,
        integer_value,
        boolean_value,
        text_value,
        clock_quality)
    SELECT source_event_id,
           'NV1',
           'NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-C06',
           NULL,
           'C06-LAB',
           'Formation/Voltage',
           :'c06_expired_time'::TIMESTAMPTZ,
           now(),
           now(),
           'real',
           3.7,
           NULL,
           NULL,
           NULL,
           'Drifted'
    FROM claim
    RETURNING source_event_id
)
INSERT INTO c06_retention_probe (source_event_id)
SELECT source_event_id FROM telemetry;

-- Migration 007 took the scheduled retention job away until legal hold exists (M12), so the lab
-- reaches for drop_chunks itself. The check below is the half that has to stay: the danger this lab
-- demonstrates is now reachable only by an operator typing it, and if a scheduled job ever comes
-- back without a hold in front of it, this lab is where that is noticed.
SELECT count(*) AS c06_retention_jobs
FROM timescaledb_information.jobs
WHERE hypertable_schema = 'ts'
  AND hypertable_name = 'telemetry_measurement'
  AND proc_name = 'policy_retention'
\gset

SELECT count(*) AS c06_retention_before
FROM ts.telemetry_measurement
JOIN c06_retention_probe USING (source_event_id)
\gset

SELECT count(*) AS c06_expired_chunk_rows_before
FROM ts.telemetry_measurement
WHERE device_timestamp >= date_trunc('day', :'c06_expired_time'::TIMESTAMPTZ)
  AND device_timestamp < date_trunc('day', :'c06_expired_time'::TIMESTAMPTZ) + INTERVAL '1 day'
\gset

SELECT drop_chunks('ts.telemetry_measurement', older_than => INTERVAL '400 days');

SELECT count(*) AS c06_retention_after
FROM ts.telemetry_measurement
JOIN c06_retention_probe USING (source_event_id)
\gset

SELECT count(*) AS c06_retention_claim_after
FROM ingest.processed_message
JOIN c06_retention_probe USING (source_event_id)
\gset

-- Replaying the retained claim must insert neither a new claim nor a new telemetry row.
WITH replayed_claim AS (
    INSERT INTO ingest.processed_message (
        source_event_id,
        site_id,
        natural_key,
        first_seen_at)
    SELECT source_event_id,
           'NV1',
           'c06/retention/replay',
           now()
    FROM c06_retention_probe
    ON CONFLICT (source_event_id) DO NOTHING
    RETURNING source_event_id
)
SELECT count(*) AS c06_replay_claim_inserted
FROM replayed_claim
\gset

SELECT (
           :'c06_retention_jobs' = '0'
       AND :'c06_retention_before' = '1'
       AND :'c06_retention_after' = '0'
       AND :'c06_retention_claim_after' = '1'
       AND :'c06_replay_claim_inserted' = '0'
       AND :'c06_expired_chunk_rows_before' = '1'
       AND :'c06_has_five_trial_pairs' = 'true'
       AND :'c06_valid_rowstore_chunks' = '5'
       AND :'c06_valid_compressed_chunks' = '5'
       AND :'c06_rowstore_chunks_uncompressed' = '5'
       AND :'c06_compressed_chunks_compressed' = '5'
       AND :'c06_compression_check_count' = '5'
       AND :'c06_min_compressed_rows' = '50001'
       AND :'c06_max_compressed_rows' = '50001'
       AND :'c06_min_before_compression_bytes'::BIGINT > 73728
       AND :'c06_all_trials_compressed' = 'true'
       AND :'c06_expired_chunk_isolated' = 'true'
       AND :'c06_timing_sample_count' = '10'
       AND :'c06_rowstore_sample_count' = '5'
       AND :'c06_compressed_sample_count' = '5'
       AND :'c06_min_timing_ms'::NUMERIC > 0
       AND :'c06_rowstore_median_ms'::NUMERIC > 0
       AND :'c06_compressed_median_ms'::NUMERIC > 0
       AND :'c06_paired_median_ratio'::NUMERIC > 0
       )::TEXT AS c06_lab_pass
\gset

SELECT 'rows_per_insert|' || 10000;
SELECT metric || '_trial_' || trial || '|' || round(value, 3)
FROM c06_measurements
ORDER BY trial, metric;
SELECT 'paired_ratio_trial_' || trial || '|' || round(compressed.value / nullif(rowstore.value, 0), 3)
FROM c06_measurements AS rowstore
JOIN c06_measurements AS compressed USING (trial)
WHERE rowstore.metric = 'uncompressed_insert_ms'
  AND compressed.metric = 'compressed_insert_ms'
ORDER BY trial;
SELECT 'compressed_state_trial_' || trial || '|' ||
       rows_before_late_write || ' rows|' ||
       before_total_bytes || ' before bytes|' ||
       after_total_bytes || ' after bytes|' ||
       compression_status
FROM c06_compression_checks
ORDER BY trial;
SELECT 'valid_rowstore_chunks|' || :'c06_valid_rowstore_chunks';
SELECT 'valid_compressed_chunks|' || :'c06_valid_compressed_chunks';
SELECT 'rowstore_chunks_uncompressed|' || :'c06_rowstore_chunks_uncompressed';
SELECT 'compressed_chunks_compressed|' || :'c06_compressed_chunks_compressed';
SELECT 'uncompressed_median_ms|' || round(:'c06_rowstore_median_ms'::NUMERIC, 3);
SELECT 'compressed_median_ms|' || round(:'c06_compressed_median_ms'::NUMERIC, 3);
SELECT 'median_paired_ratio|' || round(:'c06_paired_median_ratio'::NUMERIC, 3);
SELECT 'retention_age_days|' || 500;
SELECT 'retention_before|' || :'c06_retention_before';
SELECT 'retention_after|' || :'c06_retention_after';
SELECT 'retention_claim_after|' || :'c06_retention_claim_after';
SELECT 'replay_claim_inserted|' || :'c06_replay_claim_inserted';

\if :c06_lab_pass
    COMMIT;
\else
    ROLLBACK;
    \echo 'FAIL: C06 evidence is incomplete or contradicts the expected invariants.'
    \quit 3
\endif
