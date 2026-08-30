\set ON_ERROR_STOP on

SET TIME ZONE 'UTC';

\o /dev/null

-- Keep FROM and TO paired. Defaults cover seven days and stop one complete minute behind the
-- current minute, so an open bucket can never change underneath the refresh.
SELECT coalesce(
           nullif(:'rollup_to', '')::TIMESTAMPTZ,
           date_trunc('minute', clock_timestamp()) - INTERVAL '1 minute') AS c07_rollup_to
\gset

SELECT coalesce(
           nullif(:'rollup_from', '')::TIMESTAMPTZ,
           :'c07_rollup_to'::TIMESTAMPTZ - INTERVAL '7 days') AS c07_rollup_from
\gset

-- psql 17's \quit does not accept an exit code. A session-local assertion lets ON_ERROR_STOP return
-- code 3 to the shell, which is what makes every invalid range fail closed before the CALL.
CREATE FUNCTION pg_temp.c07_require(condition BOOLEAN, message TEXT)
RETURNS VOID
LANGUAGE plpgsql
AS $assert$
BEGIN
    IF NOT condition THEN
        RAISE EXCEPTION '%', message;
    END IF;
END
$assert$;

SELECT pg_temp.c07_require(
    date_trunc('minute', :'c07_rollup_from'::TIMESTAMPTZ) = :'c07_rollup_from'::TIMESTAMPTZ
    AND date_trunc('minute', :'c07_rollup_to'::TIMESTAMPTZ) = :'c07_rollup_to'::TIMESTAMPTZ,
    'ROLLUP_FROM and ROLLUP_TO must be exact UTC-minute boundaries.');

SELECT pg_temp.c07_require(
    :'c07_rollup_from'::TIMESTAMPTZ < :'c07_rollup_to'::TIMESTAMPTZ,
    'ROLLUP_FROM must be earlier than ROLLUP_TO.');

SELECT pg_temp.c07_require(
    :'c07_rollup_to'::TIMESTAMPTZ
        <= date_trunc('minute', clock_timestamp()) - INTERVAL '1 minute',
    'ROLLUP_TO must leave at least one complete minute closed.');

-- Raw telemetry is retained for 400 days. Cap a single manual operation at 399 days so the command
-- never suggests it can repair buckets after their source chunks may already have disappeared.
SELECT pg_temp.c07_require(
    :'c07_rollup_to'::TIMESTAMPTZ - :'c07_rollup_from'::TIMESTAMPTZ <= INTERVAL '399 days',
    'one wide refresh may span at most 399 days.');

-- A short interval can still be ancient. Refreshing it after raw retention has removed its source
-- could replace a 15-year aggregate with an empty result, so age is an independent safety gate.
SELECT pg_temp.c07_require(
    :'c07_rollup_from'::TIMESTAMPTZ >= clock_timestamp() - INTERVAL '399 days',
    'ROLLUP_FROM must remain inside the 399-day raw-telemetry horizon.');

SELECT pg_temp.c07_require(
    EXISTS (
        SELECT 1
        FROM timescaledb_information.continuous_aggregates
        WHERE view_schema = 'ts'
          AND view_name = 'process_signal_1m'
          AND materialized_only),
    'ts.process_signal_1m is missing or is not materialized-only. Run: make ingestion-migrate');

\o

\echo rollup_from=:c07_rollup_from
\echo rollup_to=:c07_rollup_to

CALL refresh_continuous_aggregate(
    'ts.process_signal_1m',
    :'c07_rollup_from'::TIMESTAMPTZ,
    :'c07_rollup_to'::TIMESTAMPTZ,
    force => false);

\set QUIET 1
\pset format unaligned
\pset tuples_only on
SELECT 'refreshed_seconds|' ||
       extract(epoch FROM :'c07_rollup_to'::TIMESTAMPTZ - :'c07_rollup_from'::TIMESTAMPTZ)::BIGINT;
