# ADR-001 — SQL Server là store cho event store, write model và outbox

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-26 |
| **Liên quan** | [ADR-002](ADR-002-postgresql-timescaledb-read-model.md) · `docs/scope.md` §5.5, §8.1 · ADR-003 (chưa viết) |

---

## Context

Dự án này không nhằm làm ra một MES chạy được. Mục tiêu số 3 trong khối mở đầu của `scope.md`
là **học mô hình phát triển của Siemens Opcenter Execution Foundation bằng cách tự tay dựng lại
nó**, và đó là mục tiêu chi phối mọi lựa chọn hạ tầng. Nó ràng buộc theo một cách khác thường:
**giống stack thật quan trọng hơn tối ưu cho bài toán**.

Opcenter Execution Foundation thật chạy trên SQL Server. Kiến trúc tham chiếu của AWS cho
Opcenter EF xác nhận SQL Server là primary data store — không phải suy đoán từ tài liệu
marketing.

Ràng buộc kỹ thuật đi kèm, quyết định ngay từ M0 vì sửa sau rất đắt:

- **Event store và outbox phải nằm trong cùng một transaction.** Ghi domain event rồi publish
  RabbitMQ là hai hệ thống; process chết giữa chừng là mất message hoặc mất dữ liệu. Đây là bẫy
  dual-write, và transactional outbox là cách duy nhất giải nó mà không cần distributed
  transaction (M6).
- **Event phải bất biến ở tầng DB**, không chỉ ở tầng code. Code review không bắt được một
  `UPDATE` chạy tay lúc 2 giờ sáng.
- Write model có invariant thật (recipe, material lot, equipment, factory model) cần khoá quan
  hệ và transaction, không phải eventual consistency.

## Decision

Dùng **SQL Server 2022** (`mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04`) cho:

- **event store** — append-only, optimistic concurrency theo stream;
- **write model / master data** — qua EF Core, mỗi Functional Block một `DbContext` và schema riêng;
- **outbox** — bảng message chờ publish.

Cả ba **bắt buộc nằm cùng một database** để dùng chung transaction. Đây là toàn bộ lý do outbox
tồn tại; tách nó ra store khác là xoá sạch giá trị của nó.

Quyết định này **không** bao gồm read model, telemetry và binary — xem
[ADR-002](ADR-002-postgresql-timescaledb-read-model.md).

## Consequences

**Được**

- Outbox nguyên tử thật: `INSERT` event + `INSERT` outbox row trong một transaction. Worker
  publish đọc outbox riêng, retry được, không mất message.
- `REVOKE UPDATE, DELETE` trên bảng event ép immutability ở tầng quyền, không phụ thuộc kỷ luật
  của người viết code.
- Bám đúng stack Opcenter → những gì học được ở đây mang thẳng sang dự án thật.
- EF Core migrations cho write model, quen thuộc và có tooling tốt.

**Mất / phải chịu**

- **SQL Server không có range type và `EXCLUDE` constraint.** Genealogy trace theo mét trên
  cuộn điện cực cần đúng hai thứ đó, nên read model **buộc** phải sang PostgreSQL. Quyết định
  này trực tiếp đẻ ra polyglot persistence — xem ADR-002.
- **Projection ghi PostgreSQL không cùng transaction với SQL Server.** Hệ quả bắt buộc:
  projection phải idempotent và có checkpoint, tức là replay được. Đây là ràng buộc thật, không
  phải chi tiết vặt.
- **2 GB RAM** trong ngân sách 6 GB của `mem_limit` toàn hệ (M0 §C09.4) — service ngốn nhất, và
  cũng là service khởi động lâu nhất (rủi ro R-M0-1 cho `make up` < 5 phút).
- Hai hệ migration song song: EF Core cho SQL Server, script SQL thuần cho PostgreSQL/TimescaleDB.
- Không dùng được **Marten** — nó là PostgreSQL-only. Event store phải hand-roll (~400 dòng:
  optimistic concurrency, upcasting, snapshot, checkpoint). Ghi riêng ở ADR-003.

**Việc phát sinh**

- M5: `REVOKE UPDATE, DELETE` phải nằm trong migration, có test chứng minh nó chặn thật.
- M6: outbox worker với retry và dead-letter.
- M6+: mọi projection phải có bảng checkpoint và test replay.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **PostgreSQL cho tất cả** (một store duy nhất) | Rẻ hơn hẳn về vận hành: một engine, một bộ migration, một backup, và outbox vẫn nguyên tử. Nhưng lệch khỏi stack Opcenter thật → **mất mục tiêu học chính**. Đây là phương án đúng nếu dự án này là sản phẩm thật. |
| **Marten trên PostgreSQL** | Kéo theo phương án trên, cộng thêm: mục tiêu là *hiểu cơ chế* event store chứ không phải dùng nó. Xem ADR-003. |
| **Event store riêng (EventStoreDB)** | Thêm một hệ nữa vào ngân sách RAM đã chạm 6/8 GB, và không giải được bài dual-write với write model quan hệ. |
| **Distributed transaction (MSDTC / 2PC)** giữa SQL Server và RabbitMQ | Vận hành nặng, khó test, và RabbitMQ không hỗ trợ. Outbox rẻ hơn và quan sát được. |

## Evidence

- Kiến trúc tham chiếu: *Deploying Opcenter Execution Foundation on AWS* —
  <https://docs.aws.amazon.com/solutions/deploying-siemens-opcenter-execution-foundation-on-aws/>
- Image và giới hạn tài nguyên đang chạy thật: `docker-compose.yml` — `mssql` dùng
  `mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04`, `mem_limit: 2g`.
- Bảng phân bổ dữ liệu đầy đủ: `docs/scope.md` §5.5.

Phần *"SQL Server không có range type"* là kiến thức về sản phẩm, chưa đo bằng thực nghiệm trong
repo này. Phép đo chứng minh chiều ngược lại (PostgreSQL **có** và dùng được) sẽ có ở M5 khi
dựng closure table.
