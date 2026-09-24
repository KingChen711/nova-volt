CREATE SCHEMA IF NOT EXISTS rm;

CREATE TABLE IF NOT EXISTS rm.projection_checkpoint (
    site_id text NOT NULL,
    projection_name text NOT NULL,
    last_global_seq bigint NOT NULL DEFAULT 0 CHECK (last_global_seq >= 0),
    generation bigint NOT NULL DEFAULT 0 CHECK (generation >= 0),
    PRIMARY KEY (site_id, projection_name)
);

CREATE TABLE IF NOT EXISTS rm.unit_current (
    site_id text NOT NULL,
    serial_number text NOT NULL,
    unit_kind text NOT NULL,
    product_code text NOT NULL,
    work_order_id text NOT NULL,
    routing_version text NOT NULL,
    current_step text NULL,
    operation_run_id text NULL,
    equipment_path text NULL,
    execution_state text NOT NULL,
    completed_steps jsonb NOT NULL DEFAULT '[]'::jsonb,
    stream_version bigint NOT NULL CHECK (stream_version > 0),
    last_global_seq bigint NOT NULL CHECK (last_global_seq > 0),
    PRIMARY KEY (site_id, serial_number),
    CHECK (jsonb_typeof(completed_steps) = 'array')
);

CREATE INDEX IF NOT EXISTS ix_unit_current_wip
    ON rm.unit_current(site_id, work_order_id, execution_state);

-- Delivery order and SQL identity order can differ. Retain each original event until
-- the preceding stream version exists; acknowledge projection in its PostgreSQL transaction.
CREATE TABLE IF NOT EXISTS rm.unit_projection_inbox (
    site_id text NOT NULL,
    source_event_id uuid NOT NULL,
    stream_id text NOT NULL,
    stream_version bigint NOT NULL CHECK (stream_version > 0),
    global_sequence bigint NOT NULL CHECK (global_sequence > 0),
    fact jsonb NOT NULL,
    applied boolean NOT NULL DEFAULT false,
    PRIMARY KEY (site_id, source_event_id),
    UNIQUE (site_id, stream_id, stream_version),
    UNIQUE (site_id, global_sequence)
);

CREATE INDEX IF NOT EXISTS ix_unit_projection_inbox_pending
    ON rm.unit_projection_inbox(site_id, global_sequence) WHERE NOT applied;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_projection') THEN
        GRANT USAGE ON SCHEMA rm TO nvm_projection;
        GRANT SELECT, INSERT, UPDATE ON rm.unit_current, rm.projection_checkpoint TO nvm_projection;
        GRANT SELECT, INSERT ON rm.unit_projection_inbox TO nvm_projection;
        GRANT UPDATE (applied) ON rm.unit_projection_inbox TO nvm_projection;
    END IF;
END $$;
