-- M13 (ADR-049): trace context của publish MQTT đi theo intent trong outbox, để event MeasurementRecorded lên bus trong
-- cùng trace với thiết bị. Không phải dữ liệu nghiệp vụ; NULL với dòng cũ và nguồn không có trace (file drop).
ALTER TABLE ingest.measurement_outbox ADD COLUMN trace_parent TEXT NULL
    CHECK (trace_parent IS NULL OR char_length(trace_parent) = 55);
