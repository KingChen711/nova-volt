-- M13 (ADR-049): trace W3C của command đi theo dòng outbox, để lượt publish sau commit (process khác, lúc khác) vẫn
-- nằm trong cùng trace với request đã tạo ra event. NULL khi event được ghi ngoài trace (bulk load, migration cũ).
-- Chạy lại được: chỉ thêm cột khi chưa có. Runtime đã có INSERT trên cả bảng nên không cần grant mới.
IF COL_LENGTH('es.Outbox', 'TraceParent') IS NULL
    ALTER TABLE es.Outbox ADD TraceParent varchar(55) COLLATE Latin1_General_100_BIN2 NULL;
