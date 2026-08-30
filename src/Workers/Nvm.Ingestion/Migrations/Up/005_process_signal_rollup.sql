-- The raw hypertable carries every MetricValue kind. This compatibility view deliberately exposes
-- only real-valued process signals: averaging a cell serial, a boolean state or a step index would
-- produce a number with no manufacturing meaning.
CREATE VIEW ts.process_signal AS
SELECT device_timestamp AS time,
       site_id,
       equipment_id,
       signal_code,
       unit_id,
       real_value AS value,
       clock_quality
FROM ts.telemetry_measurement
WHERE value_kind = 'real';

COMMENT ON VIEW ts.process_signal IS
    'Real-valued process signals projected from raw telemetry; every read must filter site_id.';

-- Build directly on the hypertable. TimescaleDB does not permit a continuous aggregate over the
-- compatibility view above. materialized_only makes the stored rollup observable as its own data
-- product: a query cannot silently fill a stale bucket from the raw tail and hide reconciliation
-- failures. WITH NO DATA also keeps migration time independent of the current telemetry volume.
CREATE MATERIALIZED VIEW ts.process_signal_1m
WITH (timescaledb.continuous, timescaledb.materialized_only = true) AS
SELECT time_bucket(INTERVAL '1 minute', device_timestamp) AS bucket,
       site_id,
       equipment_id,
       signal_code,
       avg(real_value) AS avg_value,
       min(real_value) AS min_value,
       max(real_value) AS max_value,
       count(*) AS sample_count
FROM ts.telemetry_measurement
WHERE value_kind = 'real'
GROUP BY bucket, site_id, equipment_id, signal_code
WITH NO DATA;

-- Five hours is a modelled operating range, not a hard upper bound: two hours of gateway
-- backlog + two hours of simulator clock skew + one hour of margin. The persistent gateway buffer
-- is byte-capped, not age-capped, so ADR-032 also requires a bounded wide refresh and reconciliation.
SELECT add_continuous_aggregate_policy(
    'ts.process_signal_1m',
    start_offset => INTERVAL '5 hours',
    end_offset => INTERVAL '1 minute',
    schedule_interval => INTERVAL '1 minute');

-- Process engineers need raw points for recent investigation; an auditor can ask for a historical
-- operating range years later. The latter question is answerable from one-minute aggregates.
SELECT add_retention_policy(
    'ts.process_signal_1m',
    drop_after => INTERVAL '15 years');
