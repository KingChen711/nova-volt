-- Inbox dùng chung cho các projection từ M7: mỗi projection giữ thứ tự version theo stream nguồn.
CREATE TABLE IF NOT EXISTS rm.projection_inbox (
    projection_name text NOT NULL,
    site_id text NOT NULL,
    source_event_id uuid NOT NULL,
    stream_id text NOT NULL,
    stream_version bigint NOT NULL CHECK (stream_version > 0),
    global_sequence bigint NOT NULL CHECK (global_sequence > 0),
    fact jsonb NOT NULL,
    applied boolean NOT NULL DEFAULT false,
    PRIMARY KEY (projection_name, site_id, source_event_id),
    UNIQUE (projection_name, site_id, stream_id, stream_version)
);
CREATE INDEX IF NOT EXISTS ix_projection_inbox_pending
    ON rm.projection_inbox (projection_name, site_id, global_sequence) WHERE NOT applied;

CREATE TABLE IF NOT EXISTS rm.projection_stream_progress (
    projection_name text NOT NULL,
    site_id text NOT NULL,
    stream_id text NOT NULL,
    stream_version bigint NOT NULL CHECK (stream_version > 0),
    PRIMARY KEY (projection_name, site_id, stream_id)
);

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_projection') THEN
        GRANT SELECT, INSERT ON rm.projection_inbox TO nvm_projection;
        GRANT UPDATE (applied) ON rm.projection_inbox TO nvm_projection;
        GRANT SELECT, INSERT, UPDATE ON rm.projection_stream_progress TO nvm_projection;
    END IF;
END $$;
