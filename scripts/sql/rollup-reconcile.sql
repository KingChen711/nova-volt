\set ON_ERROR_STOP on

SET TIME ZONE 'UTC';
SET client_min_messages = warning;
SET lock_timeout = '30s';
SET statement_timeout = '10min';

\o /dev/null

-- Serialize this lab's catalog snapshots and output. The Timescale policy scheduler remains free to
-- run; the selected minute is deliberately outside its five-hour refresh window.
SELECT pg_advisory_lock(hashtextextended('novavolt.c11.rollup-reconcile', 0));

CREATE FUNCTION pg_temp.c11_require(condition BOOLEAN, message TEXT, hint TEXT DEFAULT NULL)
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

CREATE TEMP TABLE c11_run (
    run_id       UUID        PRIMARY KEY,
    site_id      TEXT        NOT NULL CHECK (site_id = 'NV1'),
    machine_id   TEXT        NOT NULL UNIQUE,
    equipment_id TEXT        NOT NULL UNIQUE,
    signal_code  TEXT        NOT NULL,
    bucket_start TIMESTAMPTZ NOT NULL,
    bucket_end   TIMESTAMPTZ NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL,
    CHECK (bucket_end = bucket_start + INTERVAL '1 minute')
);

WITH identity AS (
    SELECT gen_random_uuid() AS run_id,
           clock_timestamp() AS created_at
), chosen AS (
    SELECT run_id,
           created_at,
           date_trunc('minute', created_at) - INTERVAL '6 hours' AS bucket_start
    FROM identity
)
INSERT INTO c11_run (
    run_id,
    site_id,
    machine_id,
    equipment_id,
    signal_code,
    bucket_start,
    bucket_end,
    created_at)
SELECT run_id,
       'NV1',
       'NOVAVOLT/NV1/FORMATION/F1/FORM-C11-' || upper(replace(run_id::TEXT, '-', '')),
       'NOVAVOLT/NV1/FORMATION/F1/FORM-C11-' || upper(replace(run_id::TEXT, '-', '')) ||
           '/CH-0001',
       'Formation/Temperature',
       bucket_start,
       bucket_start + INTERVAL '1 minute',
       created_at
FROM chosen;

CREATE TEMP TABLE c11_aggregate AS
SELECT aggregates.materialization_hypertable_schema,
       aggregates.materialization_hypertable_name
FROM timescaledb_information.continuous_aggregates AS aggregates
WHERE aggregates.view_schema = 'ts'
  AND aggregates.view_name = 'process_signal_1m'
  AND aggregates.hypertable_schema = 'ts'
  AND aggregates.hypertable_name = 'telemetry_measurement'
  AND aggregates.materialized_only;

SELECT pg_temp.c11_require(
    (SELECT count(*) FROM c11_aggregate) = 1,
    'C11 requires one materialized-only ts.process_signal_1m sourced from ts.telemetry_measurement.',
    'Run: make ingestion-migrate');

CREATE TEMP TABLE c11_machine_aggregate AS
SELECT aggregates.materialization_hypertable_schema,
       aggregates.materialization_hypertable_name
FROM timescaledb_information.continuous_aggregates AS aggregates
WHERE aggregates.view_schema = 'ts'
  AND aggregates.view_name = 'process_signal_machine_1m'
  AND aggregates.materialized_only;

SELECT pg_temp.c11_require(
    (SELECT count(*) FROM c11_machine_aggregate) = 1,
    'C11 requires one materialized-only ts.process_signal_machine_1m.',
    'Run: make ingestion-migrate');

CREATE TEMP TABLE c11_job AS
SELECT jobs.job_id,
       jobs.schedule_interval,
       jobs.scheduled,
       (jobs.config ->> 'start_offset')::INTERVAL AS start_offset,
       (jobs.config ->> 'end_offset')::INTERVAL AS end_offset
FROM timescaledb_information.jobs AS jobs
WHERE jobs.proc_schema = '_timescaledb_functions'
  AND jobs.proc_name = 'policy_refresh_continuous_aggregate'
  AND jobs.hypertable_schema = 'ts'
  AND jobs.hypertable_name = 'process_signal_1m';

SELECT pg_temp.c11_require(
    (SELECT count(*) FROM c11_job) = 1,
    'C11 requires exactly one actual refresh policy job for ts.process_signal_1m.');

SELECT pg_temp.c11_require(
    jobs.scheduled
    AND jobs.schedule_interval = INTERVAL '1 minute'
    AND jobs.start_offset = INTERVAL '5 hours'
    AND jobs.end_offset = INTERVAL '1 minute',
    format(
        'C11 policy contract changed: scheduled=%s schedule=%s start_offset=%s end_offset=%s.',
        jobs.scheduled,
        jobs.schedule_interval,
        jobs.start_offset,
        jobs.end_offset),
    'Reconcile assumptions must change with the policy; do not silently widen this lab.')
FROM c11_job AS jobs;

CREATE TEMP TABLE c11_machine_job AS
SELECT jobs.job_id,
       jobs.schedule_interval,
       jobs.scheduled,
       (jobs.config ->> 'start_offset')::INTERVAL AS start_offset,
       (jobs.config ->> 'end_offset')::INTERVAL AS end_offset
FROM timescaledb_information.jobs AS jobs
WHERE jobs.proc_schema = '_timescaledb_functions'
  AND jobs.proc_name = 'policy_refresh_continuous_aggregate'
  AND jobs.hypertable_schema = 'ts'
  AND jobs.hypertable_name = 'process_signal_machine_1m';

SELECT pg_temp.c11_require(
    (SELECT count(*) FROM c11_machine_job) = 1,
    'C11 requires exactly one actual refresh policy job for ts.process_signal_machine_1m.');

SELECT pg_temp.c11_require(
    jobs.scheduled
    AND jobs.schedule_interval = INTERVAL '1 minute'
    AND jobs.start_offset = INTERVAL '5 hours'
    AND jobs.end_offset = INTERVAL '2 minutes',
    format(
        'C11 machine policy contract changed: scheduled=%s schedule=%s start_offset=%s end_offset=%s.',
        jobs.scheduled,
        jobs.schedule_interval,
        jobs.start_offset,
        jobs.end_offset),
    'Reconcile assumptions must change with the policy; do not silently widen this lab.')
FROM c11_machine_job AS jobs;

SELECT pg_temp.c11_require(
    run.bucket_start = date_trunc('minute', run.bucket_start)
    AND run.bucket_end <= run.created_at - jobs.start_offset
    AND run.bucket_start >= run.created_at - INTERVAL '1 day',
    'C11 failed to choose a closed, exact minute outside the five-hour policy window.')
FROM c11_run AS run
CROSS JOIN c11_job AS jobs;

CREATE TEMP TABLE c11_state (
    ordinal              INTEGER     PRIMARY KEY,
    stage                TEXT        NOT NULL UNIQUE,
    raw_count            BIGINT      NOT NULL,
    parent_count         BIGINT      NOT NULL,
    machine_count        BIGINT      NOT NULL,
    raw_parent_delta     BIGINT      NOT NULL,
    parent_machine_delta BIGINT      NOT NULL
);

CREATE FUNCTION pg_temp.c11_counts(
    OUT raw_count BIGINT,
    OUT parent_count BIGINT,
    OUT machine_count BIGINT)
RETURNS RECORD
LANGUAGE plpgsql
AS $counts$
BEGIN
    SELECT count(*)
    INTO STRICT raw_count
    FROM ts.telemetry_measurement AS measurements
    JOIN c11_run AS run
      ON measurements.site_id = run.site_id
     AND measurements.equipment_id = run.equipment_id
     AND measurements.signal_code = run.signal_code
     AND measurements.device_timestamp >= run.bucket_start
     AND measurements.device_timestamp < run.bucket_end
    WHERE measurements.site_id = 'NV1'
      AND measurements.value_kind = 'real';

    SELECT coalesce(sum(rollup.sample_count), 0)::BIGINT
    INTO STRICT parent_count
    FROM ts.process_signal_1m AS rollup
    JOIN c11_run AS run
      ON rollup.site_id = run.site_id
     AND rollup.equipment_id = run.equipment_id
     AND rollup.signal_code = run.signal_code
     AND rollup.bucket >= run.bucket_start
     AND rollup.bucket < run.bucket_end
    WHERE rollup.site_id = 'NV1';

    SELECT coalesce(sum(rollup.sample_count), 0)::BIGINT
    INTO STRICT machine_count
    FROM ts.process_signal_machine_1m AS rollup
    JOIN c11_run AS run
      ON rollup.site_id = run.site_id
     AND rollup.machine_id = run.machine_id
     AND rollup.signal_code = run.signal_code
     AND rollup.bucket >= run.bucket_start
     AND rollup.bucket < run.bucket_end
    WHERE rollup.site_id = 'NV1';
END
$counts$;

CREATE FUNCTION pg_temp.c11_capture_state(state_ordinal INTEGER, state_name TEXT)
RETURNS VOID
LANGUAGE plpgsql
AS $capture$
DECLARE
    observed RECORD;
BEGIN
    SELECT * INTO STRICT observed FROM pg_temp.c11_counts();

    INSERT INTO c11_state (
        ordinal,
        stage,
        raw_count,
        parent_count,
        machine_count,
        raw_parent_delta,
        parent_machine_delta)
    VALUES (
        state_ordinal,
        state_name,
        observed.raw_count,
        observed.parent_count,
        observed.machine_count,
        observed.raw_count - observed.parent_count,
        observed.parent_count - observed.machine_count);
END
$capture$;

-- A mismatch from this checker has its own SQLSTATE. The negative control catches only that state;
-- a missing relation, malformed query, permission error, or any other SQL failure is rethrown.
CREATE FUNCTION pg_temp.c11_require_reconciled(check_name TEXT)
RETURNS VOID
LANGUAGE plpgsql
AS $checker$
DECLARE
    observed RECORD;
BEGIN
    SELECT * INTO STRICT observed FROM pg_temp.c11_counts();

    IF observed.raw_count <> observed.parent_count
       OR observed.parent_count <> observed.machine_count THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P1101',
            MESSAGE = format(
                'C11 reconciliation mismatch at %s: raw=%s parent=%s machine=%s raw_parent_delta=%s parent_machine_delta=%s.',
                check_name,
                observed.raw_count,
                observed.parent_count,
                observed.machine_count,
                observed.raw_count - observed.parent_count,
                observed.parent_count - observed.machine_count);
    END IF;
END
$checker$;

-- Normalize the selected minute before inserting anything. The unique equipment identity makes
-- both sides empty; force=true also proves the baseline is a freshly materialized zero.
SELECT bucket_start AS c11_bucket_start,
       bucket_end AS c11_bucket_end
FROM c11_run
\gset

CALL refresh_continuous_aggregate(
    'ts.process_signal_1m',
    :'c11_bucket_start'::TIMESTAMPTZ,
    :'c11_bucket_end'::TIMESTAMPTZ,
    force => true);

CALL refresh_continuous_aggregate(
    'ts.process_signal_machine_1m',
    :'c11_bucket_start'::TIMESTAMPTZ,
    :'c11_bucket_end'::TIMESTAMPTZ,
    force => true);

SELECT pg_temp.c11_capture_state(1, 'baseline');

SELECT pg_temp.c11_require(
    state.raw_count = 0
    AND state.parent_count = 0
    AND state.machine_count = 0
    AND state.raw_parent_delta = 0
    AND state.parent_machine_delta = 0,
    format(
        'C11 baseline is not isolated: raw=%s parent=%s machine=%s raw_parent_delta=%s parent_machine_delta=%s.',
        state.raw_count,
        state.parent_count,
        state.machine_count,
        state.raw_parent_delta,
        state.parent_machine_delta))
FROM c11_state AS state
WHERE state.stage = 'baseline';

CREATE TEMP TABLE c11_probe (
    sample_number  INTEGER PRIMARY KEY CHECK (sample_number BETWEEN 1 AND 3),
    source_event_id UUID    NOT NULL UNIQUE
);

INSERT INTO c11_probe (sample_number, source_event_id)
SELECT sample_number, gen_random_uuid()
FROM generate_series(1, 3) AS samples(sample_number);

CREATE TEMP TABLE c11_insert_audit (
    target_table TEXT   PRIMARY KEY,
    inserted_rows BIGINT NOT NULL
);

-- Claims and measurements are one atomic append. There is intentionally no ON CONFLICT, UPDATE,
-- DELETE, or cleanup path: a rerun receives a new run UUID and leaves this evidence intact.
BEGIN;

WITH inserted AS (
    INSERT INTO ingest.processed_message (
        source_event_id,
        site_id,
        natural_key,
        first_seen_at)
    SELECT probe.source_event_id,
           run.site_id,
           'c11/' || run.run_id::TEXT || '/' || probe.sample_number,
           clock_timestamp()
    FROM c11_probe AS probe
    CROSS JOIN c11_run AS run
    RETURNING source_event_id
)
INSERT INTO c11_insert_audit (target_table, inserted_rows)
SELECT 'ingest.processed_message', count(*)
FROM inserted;

WITH inserted AS (
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
    SELECT probe.source_event_id,
           run.site_id,
           run.equipment_id,
           NULL,
           'C11-LAB',
           run.signal_code,
           run.bucket_start + probe.sample_number * INTERVAL '15 seconds',
           clock_timestamp(),
           clock_timestamp(),
           'real',
           3.60 + probe.sample_number / 100.0,
           NULL,
           NULL,
           NULL,
           'Drifted'
    FROM c11_probe AS probe
    CROSS JOIN c11_run AS run
    JOIN ingest.processed_message AS claims
      ON claims.source_event_id = probe.source_event_id
     AND claims.site_id = run.site_id
    RETURNING source_event_id
)
INSERT INTO c11_insert_audit (target_table, inserted_rows)
SELECT 'ts.telemetry_measurement', count(*)
FROM inserted;

SELECT pg_temp.c11_require(
    (SELECT count(*) FROM c11_insert_audit WHERE inserted_rows = 3) = 2,
    'C11 must append exactly three claims and exactly three telemetry rows.');

COMMIT;

SELECT pg_temp.c11_capture_state(2, 'after_late_insert');

SELECT pg_temp.c11_require(
    state.raw_count = 3
    AND state.parent_count = 0
    AND state.machine_count = 0
    AND state.raw_parent_delta = 3
    AND state.parent_machine_delta = 0,
    format(
        'C11 late-insert state must be raw/parent/machine=3/0/0; found %s/%s/%s.',
        state.raw_count,
        state.parent_count,
        state.machine_count))
FROM c11_state AS state
WHERE state.stage = 'after_late_insert';

CREATE TEMP TABLE c11_job_stats_before AS
SELECT stats.job_id,
       coalesce(stats.total_runs, 0)::BIGINT AS total_runs,
       coalesce(stats.total_successes, 0)::BIGINT AS total_successes,
       coalesce(stats.total_failures, 0)::BIGINT AS total_failures
FROM timescaledb_information.job_stats AS stats
JOIN c11_job AS jobs USING (job_id);

SELECT pg_temp.c11_require(
    (SELECT count(*) FROM c11_job_stats_before) = 1,
    'C11 could not snapshot the refresh policy job statistics.');

CREATE TEMP TABLE c11_job_invocation (
    invoked_at TIMESTAMPTZ NOT NULL,
    deadline_at TIMESTAMPTZ NOT NULL
);

INSERT INTO c11_job_invocation
SELECT clock_timestamp(), clock_timestamp() + INTERVAL '90 seconds';

CREATE TEMP TABLE c11_job_stats_after (
    job_id                  INTEGER     PRIMARY KEY,
    last_run_started_at     TIMESTAMPTZ NOT NULL,
    last_successful_finish  TIMESTAMPTZ NOT NULL,
    last_run_status         TEXT        NOT NULL,
    total_runs              BIGINT      NOT NULL,
    total_successes         BIGINT      NOT NULL,
    total_failures          BIGINT      NOT NULL
);

-- run_job(job_id) is foreground but, in TimescaleDB 2.29.2, it does not advance job_stats. Wait
-- for the real scheduled policy execution instead. Each one-second query observes catalog state;
-- no policy setting is paused, widened, updated, or otherwise mutated by this lab.
DO $wait_for_policy$
DECLARE
    observed RECORD;
    baseline RECORD;
    invocation RECORD;
BEGIN
    SELECT * INTO STRICT baseline FROM c11_job_stats_before;
    SELECT * INTO STRICT invocation FROM c11_job_invocation;

    LOOP
        SELECT stats.job_id,
               stats.last_run_started_at,
               stats.last_successful_finish,
               stats.last_run_status,
               stats.total_runs,
               stats.total_successes,
               stats.total_failures
        INTO STRICT observed
        FROM timescaledb_information.job_stats AS stats
        JOIN c11_job AS jobs USING (job_id);

        IF observed.total_runs >= baseline.total_runs + 1
           AND observed.total_successes >= baseline.total_successes + 1
           AND observed.total_failures = baseline.total_failures
           AND observed.last_run_status = 'Success'
           AND observed.last_run_started_at >= invocation.invoked_at
           AND observed.last_successful_finish >= invocation.invoked_at THEN
            INSERT INTO c11_job_stats_after (
                job_id,
                last_run_started_at,
                last_successful_finish,
                last_run_status,
                total_runs,
                total_successes,
                total_failures)
            VALUES (
                observed.job_id,
                observed.last_run_started_at,
                observed.last_successful_finish,
                observed.last_run_status,
                observed.total_runs,
                observed.total_successes,
                observed.total_failures);
            EXIT;
        END IF;

        IF observed.total_failures <> baseline.total_failures THEN
            RAISE EXCEPTION
                'C11 scheduled policy failed while waiting: failures % -> %, last_status=%.',
                baseline.total_failures,
                observed.total_failures,
                observed.last_run_status;
        END IF;

        IF clock_timestamp() >= invocation.deadline_at THEN
            RAISE EXCEPTION
                'C11 timed out waiting for the actual scheduled policy job: runs % -> %, successes % -> %, failures % -> %, last_status=%.',
                baseline.total_runs,
                observed.total_runs,
                baseline.total_successes,
                observed.total_successes,
                baseline.total_failures,
                observed.total_failures,
                observed.last_run_status;
        END IF;

        PERFORM pg_sleep(1);
    END LOOP;
END
$wait_for_policy$;

SELECT pg_temp.c11_require(
    after.total_runs >= before.total_runs + 1
    AND after.total_successes >= before.total_successes + 1
    AND after.total_failures = before.total_failures
    AND after.last_run_status = 'Success'
    AND after.last_run_started_at >= invocation.invoked_at
    AND after.last_successful_finish >= invocation.invoked_at,
    format(
        'C11 scheduled policy job did not advance successfully: runs %s->%s successes %s->%s failures %s->%s status=%s started=%s finished=%s invoked=%s.',
        before.total_runs,
        after.total_runs,
        before.total_successes,
        after.total_successes,
        before.total_failures,
        after.total_failures,
        after.last_run_status,
        after.last_run_started_at,
        after.last_successful_finish,
        invocation.invoked_at))
FROM c11_job_stats_before AS before
JOIN c11_job_stats_after AS after USING (job_id)
CROSS JOIN c11_job_invocation AS invocation;

SELECT pg_temp.c11_capture_state(3, 'after_policy_job');

SELECT pg_temp.c11_require(
    state.raw_count = 3
    AND state.parent_count = 0
    AND state.machine_count = 0
    AND state.raw_parent_delta = 3
    AND state.parent_machine_delta = 0,
    format(
        'C11 policy control must remain raw/parent/machine=3/0/0; found %s/%s/%s.',
        state.raw_count,
        state.parent_count,
        state.machine_count))
FROM c11_state AS state
WHERE state.stage = 'after_policy_job';

CREATE TEMP TABLE c11_negative_control (
    stage    TEXT PRIMARY KEY,
    result   TEXT NOT NULL,
    sqlstate TEXT NOT NULL
);

DO $negative_control$
BEGIN
    BEGIN
        PERFORM pg_temp.c11_require_reconciled('after_policy_job');
        RAISE EXCEPTION USING
            ERRCODE = 'P1102',
            MESSAGE = 'C11 negative control false-passed: the checker accepted raw/parent/machine=3/0/0.';
    EXCEPTION
        WHEN SQLSTATE 'P1101' THEN
            INSERT INTO c11_negative_control (stage, result, sqlstate)
            VALUES ('after_policy_job', 'expected_mismatch', 'P1101');
    END;
END
$negative_control$;

SELECT pg_temp.c11_require(
    (SELECT count(*) FROM c11_negative_control
     WHERE stage = 'after_policy_job'
       AND result = 'expected_mismatch'
       AND sqlstate = 'P1101') = 1,
    'C11 raw-to-parent negative-control mismatch was not observed exactly once.');

-- First repair the parent only. This is the exact false-pass N-M3-8 exposed: a two-level checker
-- would stop here even though the dashboard's machine rollup is still stale.
CALL refresh_continuous_aggregate(
    'ts.process_signal_1m',
    :'c11_bucket_start'::TIMESTAMPTZ,
    :'c11_bucket_end'::TIMESTAMPTZ,
    force => false);

SELECT pg_temp.c11_capture_state(4, 'after_parent_refresh');

SELECT pg_temp.c11_require(
    state.raw_count = 3
    AND state.parent_count = 3
    AND state.machine_count = 0
    AND state.raw_parent_delta = 0
    AND state.parent_machine_delta = 3,
    format(
        'C11 parent-only refresh must expose raw/parent/machine=3/3/0; found %s/%s/%s.',
        state.raw_count,
        state.parent_count,
        state.machine_count))
FROM c11_state AS state
WHERE state.stage = 'after_parent_refresh';

DO $machine_negative_control$
BEGIN
    BEGIN
        PERFORM pg_temp.c11_require_reconciled('after_parent_refresh');
        RAISE EXCEPTION USING
            ERRCODE = 'P1102',
            MESSAGE = 'C11 machine negative control false-passed: the checker accepted raw/parent/machine=3/3/0.';
    EXCEPTION
        WHEN SQLSTATE 'P1101' THEN
            INSERT INTO c11_negative_control (stage, result, sqlstate)
            VALUES ('after_parent_refresh', 'expected_mismatch', 'P1101');
    END;
END
$machine_negative_control$;

SELECT pg_temp.c11_require(
    (SELECT count(*) FROM c11_negative_control
     WHERE stage = 'after_parent_refresh'
       AND result = 'expected_mismatch'
       AND sqlstate = 'P1101') = 1,
    'C11 parent-to-machine negative-control mismatch was not observed exactly once.');

-- Complete the bounded recovery in dependency order. The child consumes the freshly repaired
-- parent; reversing these calls would materialize the stale state this lab just proved detectable.
CALL refresh_continuous_aggregate(
    'ts.process_signal_machine_1m',
    :'c11_bucket_start'::TIMESTAMPTZ,
    :'c11_bucket_end'::TIMESTAMPTZ,
    force => false);

SELECT pg_temp.c11_capture_state(5, 'after_machine_refresh');

SELECT pg_temp.c11_require(
    state.raw_count = 3
    AND state.parent_count = 3
    AND state.machine_count = 3
    AND state.raw_parent_delta = 0
    AND state.parent_machine_delta = 0,
    format(
        'C11 bounded refresh must finish raw/parent/machine=3/3/3; found %s/%s/%s.',
        state.raw_count,
        state.parent_count,
        state.machine_count))
FROM c11_state AS state
WHERE state.stage = 'after_machine_refresh';

SELECT pg_temp.c11_require_reconciled('after_machine_refresh');

SELECT pg_advisory_unlock(hashtextextended('novavolt.c11.rollup-reconcile', 0));

\o

SELECT format(
    'NVM_ROLLUP_RECONCILE run_id=%s stage=%s site_id=%s machine_id=%s equipment_id=%s bucket_start=%s bucket_end=%s raw=%s parent=%s machine=%s raw_parent_delta=%s parent_machine_delta=%s gate=pass',
    run.run_id,
    state.stage,
    run.site_id,
    run.machine_id,
    run.equipment_id,
    to_char(run.bucket_start, 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    to_char(run.bucket_end, 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    state.raw_count,
    state.parent_count,
    state.machine_count,
    state.raw_parent_delta,
    state.parent_machine_delta)
FROM c11_state AS state
CROSS JOIN c11_run AS run
ORDER BY state.ordinal;

SELECT format(
    'NVM_ROLLUP_RECONCILE_INSERT run_id=%s claims=%s telemetry_rows=%s append_only=true',
    run.run_id,
    max(audit.inserted_rows) FILTER (WHERE audit.target_table = 'ingest.processed_message'),
    max(audit.inserted_rows) FILTER (WHERE audit.target_table = 'ts.telemetry_measurement'))
FROM c11_run AS run
CROSS JOIN c11_insert_audit AS audit
GROUP BY run.run_id;

SELECT format(
    'NVM_ROLLUP_RECONCILE_JOB run_id=%s job_id=%s runs_advanced=%s successes_advanced=%s failures_advanced=%s last_status=%s started_at=%s finished_at=%s gate=pass',
    run.run_id,
    after.job_id,
    after.total_runs - before.total_runs,
    after.total_successes - before.total_successes,
    after.total_failures - before.total_failures,
    lower(after.last_run_status),
    to_char(after.last_run_started_at, 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
    to_char(after.last_successful_finish, 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'))
FROM c11_run AS run
CROSS JOIN c11_job_stats_before AS before
JOIN c11_job_stats_after AS after USING (job_id);

SELECT format(
    'NVM_ROLLUP_RECONCILE_NEGATIVE_CONTROL run_id=%s stage=%s result=%s sqlstate=%s unexpected_sql_errors=rethrow gate=pass',
    run.run_id,
    control.stage,
    control.result,
    control.sqlstate)
FROM c11_run AS run
CROSS JOIN c11_negative_control AS control;

SELECT format(
    'NVM_ROLLUP_RECONCILE_SUMMARY run_id=%s gate_status=pass site_id=%s inserted=3 policy_raw_parent_delta=3 parent_only_parent_machine_delta=3 final_raw_parent_delta=0 final_parent_machine_delta=0 refresh_order=parent_then_machine negative_controls=2 cleanup=none',
    run.run_id,
    run.site_id)
FROM c11_run AS run;
