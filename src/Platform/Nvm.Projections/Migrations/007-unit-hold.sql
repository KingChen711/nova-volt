-- Thành viên hold theo lot/cuộn (cascade). Unit bị giữ khi còn ít nhất một hold hiệu lực chứa nó.
CREATE TABLE IF NOT EXISTS rm.unit_hold (
    site_id text NOT NULL,
    serial_number text NOT NULL,
    hold_id text NOT NULL,
    PRIMARY KEY (site_id, serial_number, hold_id)
);
CREATE TABLE IF NOT EXISTS rm.hold_status (
    site_id text NOT NULL,
    hold_id text NOT NULL,
    active boolean NOT NULL,
    PRIMARY KEY (site_id, hold_id)
);

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_projection') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON rm.unit_hold, rm.hold_status TO nvm_projection;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_pom') THEN
        GRANT SELECT ON rm.unit_hold, rm.hold_status TO nvm_pom;
    END IF;
END $$;
