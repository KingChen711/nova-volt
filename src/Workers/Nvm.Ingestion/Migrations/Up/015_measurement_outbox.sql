-- One durable publish intent for each eligible telemetry row. The FK and PK share the
-- global dedup key from ingest.processed_message, independent of Timescale chunks.
CREATE TABLE ingest.measurement_outbox (
    event_id UUID PRIMARY KEY REFERENCES ingest.processed_message (source_event_id),
    site_id TEXT NOT NULL,
    payload JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    next_attempt_at TIMESTAMPTZ NOT NULL,
    attempt INTEGER NOT NULL DEFAULT 0 CHECK (attempt >= 0),
    published_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_measurement_outbox_payload_identity CHECK (
        payload->>'EventId' = event_id::text AND payload->>'SiteId' = site_id)
);

CREATE INDEX ix_measurement_outbox_pending
    ON ingest.measurement_outbox (next_attempt_at, event_id)
    WHERE published_at IS NULL;
