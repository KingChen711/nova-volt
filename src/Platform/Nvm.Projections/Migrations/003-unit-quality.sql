-- Facet QualityState do FB Quality sở hữu. Bảng riêng, không thêm cột vào rm.unit_current:
-- projection của Traceability không cần biết Quality tồn tại để dựng lại unit.
CREATE TABLE IF NOT EXISTS rm.unit_quality (
    site_id text NOT NULL,
    serial_number text NOT NULL,
    quality_state text NOT NULL,
    reason_code text NULL,
    stream_version bigint NOT NULL CHECK (stream_version > 0),
    last_global_seq bigint NOT NULL CHECK (last_global_seq > 0),
    PRIMARY KEY (site_id, serial_number),
    CHECK (quality_state IN ('Pending','Released','Held','Rework','Scrapped'))
);

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_projection') THEN
        GRANT SELECT, INSERT, UPDATE ON rm.unit_quality TO nvm_projection;
    END IF;
END $$;
