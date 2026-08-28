-- Destructive by definition. Review and run only when deliberately rolling back migration 001.
DROP TABLE IF EXISTS ts.telemetry_measurement;
DROP TABLE IF EXISTS ingest.processed_message;
