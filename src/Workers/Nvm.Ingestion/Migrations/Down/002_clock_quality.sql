-- Destructive by definition. Dropping the column throws away the record of which readings were
-- taken on a clock nobody could trust; the readings themselves stay.
DROP INDEX IF EXISTS ts.ix_telemetry_measurement_clock_quality;
ALTER TABLE ts.telemetry_measurement DROP COLUMN IF EXISTS clock_quality;
