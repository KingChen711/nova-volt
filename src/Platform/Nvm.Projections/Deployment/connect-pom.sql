-- Explicit cutover: no union with sample rows and no deletion of original fixtures.
CREATE OR REPLACE VIEW pom.production_units_read AS
SELECT u.serial_number AS id, u.site_id, u.serial_number, u.unit_kind,
    CASE WHEN length(split_part(u.equipment_path, '/', 4)) = 2
         THEN split_part(u.equipment_path, '/', 4) ELSE substring(u.serial_number, 5, 2) END AS line,
    coalesce(nullif(split_part(u.equipment_path, '/', 5), ''), u.equipment_path, '') AS resource,
    coalesce(u.equipment_path, '') AS equipment_path, u.work_order_id,
    coalesce(u.operation_run_id, '') AS operation_run_id,
    coalesce(u.current_step, '') AS step_code, u.execution_state,
    -- Facet Quality thắng; hold serial trùng có trước facet (không có UnitQuarantined) vẫn được giữ.
    coalesce(q.quality_state, CASE WHEN h.serial_number IS NOT NULL THEN 'Held' ELSE 'Pending' END) AS quality_state,
    'AtStation'::text AS location_state,
    CASE WHEN q.quality_state IN ('Held','Scrapped') THEN coalesce(q.reason_code, 'QUALITY_HOLD')
         WHEN h.serial_number IS NOT NULL THEN 'DUPLICATE_SERIAL'
         WHEN u.execution_state <> 'Running' THEN 'STEP_NOT_RUNNING' ELSE NULL END AS blocking_reason_code,
    CASE WHEN q.quality_state = 'Scrapped' THEN 'Sản phẩm đã bị loại bỏ.'
         WHEN coalesce(q.reason_code, CASE WHEN h.serial_number IS NOT NULL THEN 'DUPLICATE_SERIAL' END) = 'DUPLICATE_SERIAL'
              AND coalesce(q.quality_state, 'Held') = 'Held' THEN 'Serial trùng; sản phẩm đang bị giữ.'
         WHEN q.quality_state = 'Held' THEN 'Sản phẩm đang bị giữ chất lượng.'
         WHEN u.execution_state <> 'Running' THEN 'Công đoạn chưa chạy hoặc đã hoàn tất.' ELSE NULL END AS blocking_reason_text,
    c.read_revision AS revision
FROM rm.unit_current u
JOIN rm.projection_checkpoint c ON c.site_id = u.site_id AND c.projection_name = 'unit-current-v1'
LEFT JOIN (SELECT DISTINCT site_id, serial_number FROM rm.unit_duplicate_hold) h
    ON h.site_id = u.site_id AND h.serial_number = u.serial_number
LEFT JOIN rm.unit_quality q ON q.site_id = u.site_id AND q.serial_number = u.serial_number;

CREATE OR REPLACE VIEW pom.wip_board_read AS
SELECT site_id || ':' || line || ':' || step_code || ':' || quality_state AS id,
    site_id, line, step_code, quality_state, count(*)::integer AS unit_count, max(revision) AS revision
FROM pom.production_units_read
GROUP BY site_id, line, step_code, quality_state;

GRANT SELECT ON pom.production_units_read, pom.wip_board_read TO nvm_pom;
