-- Biến thể "bảng": trạng thái ở bảng lịch riêng, một dòng cho mỗi dòng tin, tạo bằng trigger lúc tin vào.
CREATE TABLE relay.message_schedule (
    sequence_id   bigint      PRIMARY KEY REFERENCES relay.line_message (sequence_id) ON DELETE CASCADE,
    status        text        NOT NULL DEFAULT 'NEW',
    error_code    integer     NOT NULL DEFAULT 0,
    attempt_count integer     NOT NULL DEFAULT 0,
    last_sent_on  timestamptz,
    -- Giờ thật lúc có kết quả (MOM ghi về, hoặc notify lỗi): để đo độ trễ từ lúc tin vào.
    last_result_on timestamptz
);

CREATE INDEX message_schedule_status ON relay.message_schedule (status, error_code);

CREATE FUNCTION relay.schedule_new_message() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO relay.message_schedule (sequence_id) VALUES (NEW.sequence_id);
    RETURN NEW;
END $$;

CREATE TRIGGER line_message_schedule AFTER INSERT ON relay.line_message
    FOR EACH ROW EXECUTE FUNCTION relay.schedule_new_message();

CREATE VIEW relay.message_status AS
SELECT sequence_id, status, error_code, attempt_count, last_sent_on, last_result_on
FROM relay.message_schedule;
