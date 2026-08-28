CREATE SCHEMA IF NOT EXISTS ingest;
CREATE SCHEMA IF NOT EXISTS ts;

-- Global by design: partitioning this table by first_seen_at would force that column into every
-- unique constraint and would let one source_event_id through again in a later month (ADR-030).
CREATE TABLE ingest.processed_message (
    source_event_id UUID        PRIMARY KEY,
    site_id         TEXT        NOT NULL,
    natural_key     TEXT        NOT NULL,
    first_seen_at   TIMESTAMPTZ NOT NULL
);

CREATE INDEX ix_processed_message_site_seen
    ON ingest.processed_message (site_id, first_seen_at DESC);

-- M2 keeps a row representation that can carry every MetricValue case. M3 owns conversion to a
-- hypertable, compression and numeric process-signal projections; none of those belongs here.
CREATE TABLE ts.telemetry_measurement (
    source_event_id    UUID        PRIMARY KEY
        REFERENCES ingest.processed_message (source_event_id),
    site_id            TEXT        NOT NULL,
    equipment_id       TEXT        NOT NULL,
    unit_id            TEXT        NULL,
    step_code          TEXT        NOT NULL,
    signal_code        TEXT        NOT NULL CHECK (char_length(signal_code) <= 256),
    device_timestamp   TIMESTAMPTZ NOT NULL,
    gateway_timestamp  TIMESTAMPTZ NOT NULL,
    recorded_at        TIMESTAMPTZ NOT NULL,
    value_kind         TEXT        NOT NULL
        CHECK (value_kind IN ('real', 'integer', 'boolean', 'text', 'absent')),
    real_value         DOUBLE PRECISION NULL,
    integer_value      BIGINT      NULL,
    boolean_value      BOOLEAN     NULL,
    text_value         TEXT        NULL,
    CONSTRAINT ck_telemetry_value_shape CHECK (
        (value_kind = 'real'    AND real_value    IS NOT NULL AND integer_value IS NULL AND boolean_value IS NULL AND text_value IS NULL) OR
        (value_kind = 'integer' AND integer_value IS NOT NULL AND real_value    IS NULL AND boolean_value IS NULL AND text_value IS NULL) OR
        (value_kind = 'boolean' AND boolean_value IS NOT NULL AND real_value    IS NULL AND integer_value IS NULL AND text_value IS NULL) OR
        (value_kind = 'text'    AND text_value    IS NOT NULL AND real_value    IS NULL AND integer_value IS NULL AND boolean_value IS NULL) OR
        (value_kind = 'absent'  AND real_value    IS NULL     AND integer_value IS NULL AND boolean_value IS NULL AND text_value IS NULL)
    )
);

CREATE INDEX ix_telemetry_measurement_site_device_time
    ON ts.telemetry_measurement (site_id, equipment_id, device_timestamp DESC);
