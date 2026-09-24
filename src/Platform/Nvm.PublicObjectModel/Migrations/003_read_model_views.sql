-- Stable POM query surfaces. The explicit projection deployment replaces their
-- definitions with live read models; legacy fixture tables remain intact.
CREATE VIEW pom.production_units_read AS SELECT id::text, site_id::text, serial_number::text,
    unit_kind::text, line::text, resource::text, equipment_path::text, work_order_id::text,
    operation_run_id::text, step_code::text, execution_state::text, quality_state::text,
    location_state::text, blocking_reason_code::text, blocking_reason_text::text, revision
    FROM pom.production_units;
CREATE VIEW pom.wip_board_read AS SELECT id::text, site_id::text, line::text, step_code::text,
    quality_state::text, unit_count, revision FROM pom.wip_board;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nvm_pom') THEN
        GRANT SELECT ON pom.production_units_read, pom.wip_board_read TO nvm_pom;
    END IF;
END $$;
