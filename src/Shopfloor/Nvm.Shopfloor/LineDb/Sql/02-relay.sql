-- Bảng trung gian: mỗi kết quả máy một dòng, do trigger của bảng máy chép sang. Trạng thái của dòng nằm ở chỗ riêng
-- (cột theo môi trường hoặc bảng lịch), đọc qua view relay.message_status: xem 03-state-*.sql.
CREATE TABLE relay.line_message (
    sequence_id    bigserial PRIMARY KEY,
    area           text        NOT NULL,
    source_table   text        NOT NULL,
    operation_code text        NOT NULL,
    equipment_code text        NOT NULL,
    serial         text        NOT NULL,
    payload        jsonb       NOT NULL,
    produced_on    timestamptz NOT NULL,
    created_on     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX line_message_operation ON relay.line_message (operation_code, sequence_id);

-- Chính sách gửi lại, khóa theo cặp (trạng thái, mã lỗi ghép). Là dữ liệu: môi trường thật có thể có thêm dòng.
-- Đường relay chỉ gửi lại dòng có cặp khớp; đường poll gửi lại cả dòng không khớp, mặc định 1 lần sau 10 phút.
CREATE TABLE relay.retry_policy (
    status         text     NOT NULL,
    error_code     integer  NOT NULL,
    max_attempts   integer  NOT NULL,
    retry_interval interval NOT NULL,
    PRIMARY KEY (status, error_code)
);

INSERT INTO relay.retry_policy (status, error_code, max_attempts, retry_interval) VALUES
    -- Lỗi chung của từng loại xử lý (ghi operation, hao hụt, hàng lỗi).
    ('RETRY', -6999, 5, interval '10 minutes'),
    ('RETRY', -5999, 3, interval '10 minutes'),
    ('RETRY', -4999, 3, interval '10 minutes'),
    -- Giả định (ghi ở oef-mapping): dòng đã gửi mà không có kết quả (MOM dừng, relay mất notify) được gửi lại.
    ('SENT', 0, 3, interval '10 minutes');
