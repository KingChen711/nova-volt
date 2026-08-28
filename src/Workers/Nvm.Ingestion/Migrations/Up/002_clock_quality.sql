-- A reading from a device whose clock is wrong is still a reading. It is stored, and it says so.
--
-- NOT NULL with no default: every write path must decide. A default would let a future adapter that
-- forgot to classify produce rows claiming Good, which is worse than a row claiming nothing —
-- M3 computes production shifts from device_timestamp and would trust them.
ALTER TABLE ts.telemetry_measurement
    ADD COLUMN clock_quality TEXT NOT NULL
        CHECK (clock_quality IN ('Good', 'Drifted', 'Unknown'));

-- Partial, because the whole point is that drifted rows are rare and worth finding. A full index on
-- a column that is 'Good' for 99,9% of rows would be paid for on every insert and read on none.
CREATE INDEX ix_telemetry_measurement_clock_quality
    ON ts.telemetry_measurement (site_id, device_timestamp DESC)
    WHERE clock_quality <> 'Good';
