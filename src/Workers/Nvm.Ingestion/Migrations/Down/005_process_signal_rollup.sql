-- Remove scheduler jobs before their materialization hypertable disappears. TimescaleDB records
-- both jobs against that internal hypertable even though the information view displays the CAGG
-- name, so relying on DROP cleanup would hide whether rollback removed the intended lifecycle.
SELECT remove_retention_policy('ts.process_signal_1m', if_exists => true);
SELECT remove_continuous_aggregate_policy('ts.process_signal_1m', if_exists => true);

DROP MATERIALIZED VIEW ts.process_signal_1m;
DROP VIEW ts.process_signal;
