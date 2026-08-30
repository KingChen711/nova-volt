-- Puts the 400-day retention policy of migration 004 back on the schedule.
--
-- Reversible, but not harmless: running this restores unconditional deletion of raw telemetry chunks
-- by device_timestamp with no legal hold in front of it, which is the exact configuration 007 exists
-- to withdraw. Only run it when legal hold has landed, or when a throwaway database is about to be
-- dropped anyway.
SELECT add_retention_policy(
    'ts.telemetry_measurement',
    drop_after => INTERVAL '400 days',
    if_not_exists => true);

COMMENT ON TABLE ts.telemetry_measurement IS
    'Raw device telemetry. Hypertable on device_timestamp (ADR-011); every MetricValue kind, not just numbers.';
