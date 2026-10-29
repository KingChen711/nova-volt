-- Bảng máy theo khu. Mỗi bảng có trigger viết tay chép dòng mới sang relay.line_message theo một trong ba dạng JSON:
--   unit   một sản phẩm có serial; nguyên liệu kèm lô (materials)
--   batch  một lô có lượng (cuộn điện cực); lô nguyên liệu kèm lượng (lots)
--   shrink hao hụt của một lô nguyên liệu
-- Mã máy gửi đi = mã máy phía xưởng + '_' + số line; mã bước dùng chung cho mọi line.
-- run_class: chế độ máy làm ra đơn vị đó; 1 thường, 2 trial mode, 3 flow mode (hàng chạy thử).

-- Điện cực: máy phủ ra cuộn điện cực.
CREATE TABLE elec.coater_result (
    id          bigserial PRIMARY KEY,
    line_no     integer       NOT NULL,
    machine     text          NOT NULL,
    op_code     text          NOT NULL,
    coil_id     text          NOT NULL,
    length_m    numeric(12,3) NOT NULL,
    started_at  timestamptz   NOT NULL,
    finished_at timestamptz   NOT NULL,
    result      text          NOT NULL,
    result_code text          NOT NULL,
    run_class   smallint      NOT NULL DEFAULT 1,
    lots        jsonb         NOT NULL DEFAULT '[]'
);

CREATE FUNCTION elec.coater_result_to_relay() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = elec, pg_temp AS $$
BEGIN
    INSERT INTO relay.line_message (area, source_table, operation_code, equipment_code, serial, payload, produced_on)
    VALUES ('elec', 'elec.coater_result', NEW.op_code, NEW.machine || '_' || NEW.line_no, NEW.coil_id,
            jsonb_build_object('kind', 'batch', 'batch_id', NEW.coil_id, 'length', NEW.length_m,
                               'start', NEW.started_at, 'end', NEW.finished_at, 'result', NEW.result,
                               'code', NEW.result_code, 'class', NEW.run_class, 'lots', NEW.lots),
            NEW.finished_at);
    RETURN NEW;
END $$;

CREATE TRIGGER coater_result_relay AFTER INSERT ON elec.coater_result
    FOR EACH ROW EXECUTE FUNCTION elec.coater_result_to_relay();

-- Điện cực: hao hụt lô nguyên liệu (cắt bỏ đầu cuộn, rơi vãi).
CREATE TABLE elec.shrink_log (
    id        bigserial PRIMARY KEY,
    line_no   integer       NOT NULL,
    machine   text          NOT NULL,
    op_code   text          NOT NULL,
    item      text          NOT NULL,
    lot       text          NOT NULL,
    quantity  numeric(12,3) NOT NULL,
    unit      text          NOT NULL,
    loss_code text          NOT NULL DEFAULT '50',
    logged_at timestamptz   NOT NULL
);

CREATE FUNCTION elec.shrink_log_to_relay() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = elec, pg_temp AS $$
BEGIN
    INSERT INTO relay.line_message (area, source_table, operation_code, equipment_code, serial, payload, produced_on)
    VALUES ('elec', 'elec.shrink_log', NEW.op_code, NEW.machine || '_' || NEW.line_no, NEW.lot,
            jsonb_build_object('kind', 'shrink', 'item', NEW.item, 'lot', NEW.lot, 'qty', NEW.quantity,
                               'unit', NEW.unit, 'code', NEW.loss_code, 'at', NEW.logged_at),
            NEW.logged_at);
    RETURN NEW;
END $$;

CREATE TRIGGER shrink_log_relay AFTER INSERT ON elec.shrink_log
    FOR EACH ROW EXECUTE FUNCTION elec.shrink_log_to_relay();

-- Lắp cell: mỗi cell một dòng; nguyên liệu nằm ở cột riêng, trigger ghép thành danh sách.
CREATE TABLE cell.assembly_result (
    id             bigserial PRIMARY KEY,
    line_no        integer     NOT NULL,
    machine        text        NOT NULL,
    op_code        text        NOT NULL,
    cell_id        text        NOT NULL,
    started_at     timestamptz NOT NULL,
    finished_at    timestamptz NOT NULL,
    judge          text        NOT NULL,
    judge_code     text        NOT NULL,
    run_class      smallint    NOT NULL DEFAULT 1,
    electrode_item text        NOT NULL,
    electrode_coil text        NOT NULL,
    separator_lot  text,
    case_item      text        NOT NULL,
    case_lot       text
);

CREATE FUNCTION cell.assembly_result_to_relay() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = cell, pg_temp AS $$
DECLARE
    materials jsonb := jsonb_build_array(jsonb_build_object('item', NEW.electrode_item, 'lot', NEW.electrode_coil));
BEGIN
    -- Lô của màng ngăn và vỏ chỉ có khi máy quét được nhãn; không có thì MOM trừ từ MTU tạm.
    IF NEW.separator_lot IS NOT NULL THEN
        materials := materials || jsonb_build_object('item', 'Separator film lot in use', 'lot', NEW.separator_lot);
    END IF;
    IF NEW.case_lot IS NOT NULL THEN
        materials := materials || jsonb_build_object('item', NEW.case_item, 'lot', NEW.case_lot);
    END IF;

    INSERT INTO relay.line_message (area, source_table, operation_code, equipment_code, serial, payload, produced_on)
    VALUES ('cell', 'cell.assembly_result', NEW.op_code, NEW.machine || '_' || NEW.line_no, NEW.cell_id,
            jsonb_build_object('kind', 'unit', 'serial', NEW.cell_id, 'qty', 1, 'start', NEW.started_at,
                               'end', NEW.finished_at, 'result', NEW.judge, 'code', NEW.judge_code,
                               'class', NEW.run_class, 'materials', materials),
            NEW.finished_at);
    RETURN NEW;
END $$;

CREATE TRIGGER assembly_result_relay AFTER INSERT ON cell.assembly_result
    FOR EACH ROW EXECUTE FUNCTION cell.assembly_result_to_relay();

-- Formation: sạc xả từng cell; cell vào là nguyên liệu của cell đã formation (cùng serial).
CREATE TABLE form.charge_result (
    id          bigserial PRIMARY KEY,
    line_no     integer     NOT NULL,
    machine     text        NOT NULL,
    op_code     text        NOT NULL,
    cell_id     text        NOT NULL,
    cell_item   text        NOT NULL,
    started_at  timestamptz NOT NULL,
    finished_at timestamptz NOT NULL,
    result      text        NOT NULL,
    result_code text        NOT NULL,
    run_class   smallint    NOT NULL DEFAULT 1,
    ocv_mv      numeric(8,2)
);

CREATE FUNCTION form.charge_result_to_relay() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = form, pg_temp AS $$
BEGIN
    INSERT INTO relay.line_message (area, source_table, operation_code, equipment_code, serial, payload, produced_on)
    VALUES ('form', 'form.charge_result', NEW.op_code, NEW.machine || '_' || NEW.line_no, NEW.cell_id,
            jsonb_build_object('kind', 'unit', 'serial', NEW.cell_id, 'qty', 1, 'start', NEW.started_at,
                               'end', NEW.finished_at, 'result', NEW.result, 'code', NEW.result_code,
                               'class', NEW.run_class,
                               'materials', jsonb_build_array(jsonb_build_object('item', NEW.cell_item, 'lot', NEW.cell_id))),
            NEW.finished_at);
    RETURN NEW;
END $$;

CREATE TRIGGER charge_result_relay AFTER INSERT ON form.charge_result
    FOR EACH ROW EXECUTE FUNCTION form.charge_result_to_relay();

-- Module: ghép nhiều cell đã formation; danh sách cell là mảng, trigger đổi thành danh sách nguyên liệu.
CREATE TABLE mod.module_result (
    id          bigserial PRIMARY KEY,
    line_no     integer     NOT NULL,
    machine     text        NOT NULL,
    op_code     text        NOT NULL,
    module_id   text        NOT NULL,
    cell_item   text        NOT NULL,
    cell_ids    text[]      NOT NULL,
    started_at  timestamptz NOT NULL,
    finished_at timestamptz NOT NULL,
    result      text        NOT NULL,
    result_code text        NOT NULL,
    run_class   smallint    NOT NULL DEFAULT 1
);

CREATE FUNCTION mod.module_result_to_relay() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = mod, pg_temp AS $$
BEGIN
    INSERT INTO relay.line_message (area, source_table, operation_code, equipment_code, serial, payload, produced_on)
    VALUES ('mod', 'mod.module_result', NEW.op_code, NEW.machine || '_' || NEW.line_no, NEW.module_id,
            jsonb_build_object('kind', 'unit', 'serial', NEW.module_id, 'qty', 1, 'start', NEW.started_at,
                               'end', NEW.finished_at, 'result', NEW.result, 'code', NEW.result_code,
                               'class', NEW.run_class,
                               'materials', (SELECT COALESCE(jsonb_agg(jsonb_build_object('item', NEW.cell_item, 'lot', c) ORDER BY o), '[]')
                                             FROM unnest(NEW.cell_ids) WITH ORDINALITY AS u(c, o))),
            NEW.finished_at);
    RETURN NEW;
END $$;

CREATE TRIGGER module_result_relay AFTER INSERT ON mod.module_result
    FOR EACH ROW EXECUTE FUNCTION mod.module_result_to_relay();
