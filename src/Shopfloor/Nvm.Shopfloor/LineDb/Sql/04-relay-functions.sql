-- Hàm của bảng trung gian. Chỉ đọc và ghi trạng thái qua view relay.message_status, nên chạy như nhau với cả hai biến thể.
-- Giờ "bây giờ" do bên gọi truyền vào (p_now): MOM dùng đồng hồ của nó, test tua giờ được.

-- Lấy tối đa p_limit dòng của một mã bước: dòng NEW, và dòng SENT/RETRY tới lượt gửi lại. Đánh dấu SENT, tăng số lần gửi.
-- p_mode 'relay': chỉ gửi lại dòng có cặp (trạng thái, mã lỗi) trong chính sách (JOIN).
-- p_mode 'poll' : dòng không có chính sách vẫn được gửi lại, mặc định 1 lần sau 10 phút (LEFT JOIN + COALESCE).
CREATE FUNCTION relay.claim(p_operation text, p_limit integer, p_mode text, p_now timestamptz)
RETURNS TABLE (sequence_id bigint, area text, source_table text, operation_code text, equipment_code text, serial text,
               payload jsonb, produced_on timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = relay, pg_temp AS $$
#variable_conflict use_column
DECLARE
    ids bigint[];
BEGIN
    SELECT array_agg(p.sequence_id ORDER BY p.sequence_id) INTO ids
    FROM (
        SELECT m.sequence_id
        FROM relay.line_message m
        JOIN relay.message_status s ON s.sequence_id = m.sequence_id
        LEFT JOIN relay.retry_policy r ON r.status = s.status AND r.error_code = s.error_code
        WHERE m.operation_code = p_operation
          AND (s.status = 'NEW'
               OR (s.status IN ('SENT', 'RETRY')
                   AND (p_mode = 'poll' OR r.status IS NOT NULL)
                   AND s.attempt_count <= COALESCE(r.max_attempts, 1)
                   AND s.last_sent_on <= p_now - COALESCE(r.retry_interval, interval '10 minutes')))
        ORDER BY m.sequence_id
        LIMIT p_limit
        FOR UPDATE OF m SKIP LOCKED
    ) p;

    IF ids IS NULL THEN
        RETURN;
    END IF;

    UPDATE relay.message_status
    SET status = 'SENT', attempt_count = attempt_count + 1, last_sent_on = p_now
    WHERE sequence_id = ANY (ids);

    RETURN QUERY
    SELECT m.sequence_id, m.area, m.source_table, m.operation_code, m.equipment_code, m.serial, m.payload, m.produced_on
    FROM relay.line_message m
    WHERE m.sequence_id = ANY (ids)
    ORDER BY m.sequence_id;
END $$;

-- Ghi kết quả MOM trả cho một dòng. RETRY mà đã hết lượt theo chính sách thì thành FAILED.
CREATE FUNCTION relay.write_result(p_sequence_id bigint, p_status text, p_error_code integer)
RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = relay, pg_temp AS $$
BEGIN
    UPDATE relay.message_status s
    SET status = CASE
                     WHEN p_status = 'RETRY' AND EXISTS (
                         SELECT 1 FROM relay.retry_policy r
                         WHERE r.status = 'RETRY' AND r.error_code = p_error_code AND s.attempt_count > r.max_attempts)
                     THEN 'FAILED'
                     ELSE p_status
                 END,
        error_code = p_error_code,
        last_result_on = clock_timestamp()
    WHERE s.sequence_id = p_sequence_id;
END $$;

-- Đường relay, phần phía xưởng (lịch chạy mỗi phút): mỗi mã bước, gom tối đa p_rows dòng thành một JSON rồi notify trên
-- kênh line_messages. Notify lỗi (vd payload quá 8000 byte) thì các dòng đó thành NOTIFY_ERR và không tự gửi lại.
-- Notify chỉ tới bên nghe khi transaction của lời gọi commit; không ai nghe thì tin mất, dòng vẫn SENT.
CREATE FUNCTION relay.send_pending(p_rows integer, p_now timestamptz)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = relay, pg_temp AS $$
DECLARE
    op   text;
    ids  bigint[];
    body text;
    sent integer := 0;
BEGIN
    -- Mã bước có dòng cũ nhất đi trước: tin của khâu trước (cuộn điện cực) tới MOM trước tin dùng nó (cell).
    FOR op IN SELECT m.operation_code FROM relay.line_message m GROUP BY m.operation_code ORDER BY min(m.sequence_id) LOOP
        LOOP
            SELECT array_agg(c.sequence_id ORDER BY c.sequence_id),
                   json_build_object('rows', json_agg(c ORDER BY c.sequence_id))::text
            INTO ids, body
            FROM relay.claim(op, p_rows, 'relay', p_now) c;
            EXIT WHEN ids IS NULL;

            BEGIN
                PERFORM pg_notify('line_messages', body);
                sent := sent + cardinality(ids);
            EXCEPTION WHEN others THEN
                UPDATE relay.message_status ms SET status = 'NOTIFY_ERR', error_code = 0, last_result_on = clock_timestamp() WHERE ms.sequence_id = ANY (ids);
                RAISE WARNING 'notify failed for % row(s) of %: %', cardinality(ids), op, SQLERRM;
            END;
        END LOOP;
    END LOOP;

    RETURN sent;
END $$;

-- Mặc định Postgres cho PUBLIC gọi mọi hàm; ở đây chỉ role được cấp mới gọi (06-roles.sql).
REVOKE ALL ON FUNCTION relay.claim(text, integer, text, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION relay.write_result(bigint, text, integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION relay.send_pending(integer, timestamptz) FROM PUBLIC;
