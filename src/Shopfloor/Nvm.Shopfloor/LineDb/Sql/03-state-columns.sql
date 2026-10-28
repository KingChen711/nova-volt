-- Biến thể "cột": trạng thái là bốn cột cho mỗi môi trường trên chính bảng tin. Một LineDB phục vụ nhiều môi trường MOM,
-- mỗi môi trường đọc và ghi cột của mình. {env} do installer thay bằng môi trường của lần cài này.
ALTER TABLE relay.line_message
    ADD COLUMN status_qa          text    NOT NULL DEFAULT 'NEW',
    ADD COLUMN error_code_qa      integer NOT NULL DEFAULT 0,
    ADD COLUMN attempt_count_qa   integer NOT NULL DEFAULT 0,
    ADD COLUMN last_sent_on_qa    timestamptz,
    ADD COLUMN last_result_on_qa  timestamptz,
    ADD COLUMN status_stg         text    NOT NULL DEFAULT 'NEW',
    ADD COLUMN error_code_stg     integer NOT NULL DEFAULT 0,
    ADD COLUMN attempt_count_stg  integer NOT NULL DEFAULT 0,
    ADD COLUMN last_sent_on_stg   timestamptz,
    ADD COLUMN last_result_on_stg timestamptz,
    ADD COLUMN status_prod        text    NOT NULL DEFAULT 'NEW',
    ADD COLUMN error_code_prod    integer NOT NULL DEFAULT 0,
    ADD COLUMN attempt_count_prod integer NOT NULL DEFAULT 0,
    ADD COLUMN last_sent_on_prod  timestamptz,
    ADD COLUMN last_result_on_prod timestamptz;

CREATE VIEW relay.message_status AS
SELECT sequence_id,
       status_{env}        AS status,
       error_code_{env}    AS error_code,
       attempt_count_{env} AS attempt_count,
       last_sent_on_{env}  AS last_sent_on,
       last_result_on_{env} AS last_result_on
FROM relay.line_message;
