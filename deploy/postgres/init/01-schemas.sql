-- NovaVolt MES — khởi tạo PostgreSQL
--
-- File này chỉ chạy MỘT LẦN, lúc volume pgdata còn rỗng. Sửa nó rồi `docker compose up`
-- lại sẽ KHÔNG có tác dụng — phải `docker compose down -v` để xoá volume.
-- Từ M2 trở đi, mọi thay đổi schema đi qua migration script có version (DbUp),
-- không sửa file này nữa.

-- Extension phải tạo trước mọi thứ khác: hypertable ở M3 phụ thuộc vào nó.
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- ─────────────────────────────────────────────────────────
-- Bốn schema, tách theo trách nhiệm chứ không theo module.
-- Ranh giới này quyết định ở scope.md §5.5 và §8.2.
-- ─────────────────────────────────────────────────────────

-- Telemetry thô từ thiết bị: hypertable, compression, continuous aggregate.
-- Ghi rất nhiều, đọc theo khoảng thời gian. KHÔNG phải domain event.
CREATE SCHEMA IF NOT EXISTS ts;

-- Read model của CQRS: closure table, WIP board, OEE, dashboard.
-- Dựng lại được hoàn toàn từ event store, nên xoá cũng không mất dữ liệu gốc.
CREATE SCHEMA IF NOT EXISTS rm;

-- Đồ thị phả hệ: bảng cạnh append-only và bản đồ vị trí mét trên cuộn.
-- Tách khỏi rm vì đây là bản sao tối ưu-đọc của dữ liệu pháp lý, cần quyền riêng.
CREATE SCHEMA IF NOT EXISTS trace;

-- Dedup của ingestion: bảng khoá tự nhiên -> source_event_id.
-- Khoá sống toàn cục, không partition theo thời gian (ADR-030).
CREATE SCHEMA IF NOT EXISTS ingest;

COMMENT ON SCHEMA ts     IS 'Telemetry thô tu thiet bi (hypertable)';
COMMENT ON SCHEMA rm     IS 'Read model CQRS, dung lai duoc tu event store';
COMMENT ON SCHEMA trace  IS 'Genealogy DAG append-only';
COMMENT ON SCHEMA ingest IS 'Idempotency / dedup cua ingestion';
