-- Rollback of 003. Review before running: this rewrites the whole table.
--
-- There is no "un-hypertable" call. A hypertable goes back to being an ordinary table by copying
-- its contents into one and swapping the names, which means the cost is proportional to the data
-- and the operation is not free the way the forward migration nearly was. That asymmetry is worth
-- knowing BEFORE running 003 on a table with a hundred million rows in it, which is why it is
-- written out here rather than left as "drop and recreate".
--
-- Run inside a transaction. If it fails halfway, the plain table exists with a partial copy and the
-- hypertable is still there under its own name, and sorting that out by hand is worse than a lock.

BEGIN;

CREATE TABLE ts.telemetry_measurement_plain (LIKE ts.telemetry_measurement INCLUDING ALL);

INSERT INTO ts.telemetry_measurement_plain SELECT * FROM ts.telemetry_measurement;

DROP TABLE ts.telemetry_measurement;

ALTER TABLE ts.telemetry_measurement_plain RENAME TO telemetry_measurement;

-- LIKE ... INCLUDING ALL copies indexes, checks and defaults but NOT foreign keys, so the one
-- statement this file cannot leave out is the one that puts back "no telemetry row without a claim".
ALTER TABLE ts.telemetry_measurement
    ADD CONSTRAINT telemetry_measurement_source_event_id_fkey
    FOREIGN KEY (source_event_id) REFERENCES ingest.processed_message (source_event_id);

-- Back to the M2 shape: a single-column key, with no partitioning column to carry.
ALTER TABLE ts.telemetry_measurement
    DROP CONSTRAINT telemetry_measurement_plain_pkey;

ALTER TABLE ts.telemetry_measurement
    ADD CONSTRAINT telemetry_measurement_pkey PRIMARY KEY (source_event_id);

-- LIKE names the copied indexes after the temporary table, and create_hypertable had added one of
-- its own on the partitioning column. Left alone, a re-run of 003 would find a table that no longer
-- looks like the one 003 was written against — so the rollback puts the names back too.
DROP INDEX ts.telemetry_measurement_plain_device_timestamp_idx;

ALTER INDEX ts.telemetry_measurement_plain_site_id_equipment_id_device_tim_idx
    RENAME TO ix_telemetry_measurement_site_device_time;

ALTER INDEX ts.telemetry_measurement_plain_site_id_device_timestamp_idx
    RENAME TO ix_telemetry_measurement_clock_quality;

COMMIT;
