CREATE TABLE IF NOT EXISTS rm.unit_duplicate_hold (
    site_id text NOT NULL,
    serial_number text NOT NULL,
    incident_event_id uuid NOT NULL,
    global_sequence bigint NOT NULL,
    PRIMARY KEY(site_id, serial_number, incident_event_id)
);
ALTER TABLE rm.projection_checkpoint ADD COLUMN IF NOT EXISTS read_revision integer NOT NULL DEFAULT 0;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_projection') THEN
        GRANT SELECT, INSERT ON rm.unit_duplicate_hold TO nvm_projection;
    END IF;
END $$;
