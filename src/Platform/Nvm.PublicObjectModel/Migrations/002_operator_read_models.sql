-- C03 thêm hai read model cho Operator Station. File riêng, không sửa 001_equipment.sql:
-- migration cũ đã chạy trên DB thật, đổi tại chỗ sẽ không được DbUp áp dụng lại và làm lệch schema.

CREATE TABLE pom.production_units (
    id varchar(20) PRIMARY KEY,
    site_id varchar(3) NOT NULL,
    serial_number varchar(16) NOT NULL,
    unit_kind varchar(8) NOT NULL,
    line varchar(2) NOT NULL,
    resource varchar(32) NOT NULL,
    equipment_path varchar(256) NOT NULL,
    work_order_id varchar(32) NOT NULL,
    operation_run_id varchar(32) NOT NULL,
    step_code varchar(8) NOT NULL,
    execution_state varchar(16) NOT NULL,
    quality_state varchar(16) NOT NULL,
    location_state varchar(24) NOT NULL,
    blocking_reason_code varchar(32) NULL,
    blocking_reason_text varchar(256) NULL,
    revision integer NOT NULL
);

-- (site_id, id) phủ đúng đường paging đã ép site trước; serial phục vụ scan lookup theo site.
CREATE INDEX ix_production_units_site_id ON pom.production_units (site_id, id);
CREATE INDEX ix_production_units_site_serial ON pom.production_units (site_id, serial_number);
CREATE INDEX ix_production_units_site_group ON pom.production_units (site_id, line, step_code, quality_state);

CREATE TABLE pom.wip_board (
    id varchar(48) PRIMARY KEY,
    site_id varchar(3) NOT NULL,
    line varchar(2) NOT NULL,
    step_code varchar(8) NOT NULL,
    quality_state varchar(16) NOT NULL,
    unit_count integer NOT NULL,
    revision integer NOT NULL
);

CREATE INDEX ix_wip_board_site_id ON pom.wip_board (site_id, id);
