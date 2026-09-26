-- Tồn kho theo bin (scope §9/M8): lần grade mới nhất của mỗi cell và cell đã lắp vào module hay chưa.
-- Hai stream nguồn khác nhau (grading:*, membership:*) cập nhật hai nhóm cột riêng nên không phụ thuộc thứ tự.
CREATE TABLE IF NOT EXISTS rm.bin_inventory (
    site_id text NOT NULL,
    serial_number text NOT NULL,
    product_code text NULL,
    bin_code text NULL,
    reject_code text NULL,
    capacity_ah numeric(9,4) NULL,
    ocv_mv numeric(9,3) NULL,
    dcir_mohm numeric(9,4) NULL,
    graded_at timestamptz NULL,
    evaluation_id uuid NULL,
    rule_set text NULL,
    assembled_into text NULL,
    PRIMARY KEY (site_id, serial_number)
);
CREATE INDEX IF NOT EXISTS ix_bin_inventory_available
    ON rm.bin_inventory (site_id, product_code, bin_code) WHERE assembled_into IS NULL AND bin_code IS NOT NULL;

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_projection') THEN
        GRANT SELECT, INSERT, UPDATE ON rm.bin_inventory TO nvm_projection;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_pom') THEN
        GRANT USAGE ON SCHEMA rm TO nvm_pom;
        GRANT SELECT ON rm.bin_inventory, rm.unit_quality TO nvm_pom;
    END IF;
END $$;
