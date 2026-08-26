# ADR-002 — PostgreSQL + TimescaleDB cho telemetry và read model

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-26 |
| **Liên quan** | [ADR-001](ADR-001-sql-server-event-store.md) · `docs/scope.md` §5.5, §6.4, §8.2, §8.3 · ADR-006 (chưa viết) |

---

## Context

[ADR-001](ADR-001-sql-server-event-store.md) chốt SQL Server cho event store và write model. Câu
hỏi còn lại: **read model và telemetry nằm ở đâu?** Để mặc định trong SQL Server là lựa chọn rẻ
nhất về vận hành, nên nó phải bị bác bỏ bằng lý do cụ thể chứ không phải bằng sở thích.

Có hai bài toán mà read model của MES pin xe điện bắt buộc phải giải:

**1. Truy vết theo khoảng mét trên cuộn điện cực.** Một cuộn dài vài nghìn mét bị cắt thành
nhiều đoạn; mỗi cell mang một khoảng `[from_meter, to_meter)`. Câu hỏi thật của nhà máy khi có
sự cố là *"đoạn 1250–1430 m của cuộn này đã đi vào những cell nào?"* — tức là một phép **giao
khoảng**, chạy trên bảng lớn, và phải nhanh.

Với hai cột `from_meter` / `to_meter`, truy vấn là `from < 1430 AND to > 1250`. B-tree index
giúp rất ít khi các khoảng nhỏ nằm rải rác — planner phải quét rộng rồi lọc. Thứ giải đúng bài
này là **range type có index**: `numrange` + toán tử overlap `&&` + index **GiST**, index trực
tiếp lên phép giao.

Kèm theo là thứ quan trọng không kém: **`EXCLUDE` constraint** chặn hai bản ghi có khoảng chồng
lấn **ngay ở tầng DB**. Lỗi dữ liệu chồng lấn là loại code review không bao giờ bắt nổi, và trong
hệ traceability nó làm hỏng toàn bộ chuỗi quy trách nhiệm.

**SQL Server không có cả hai thứ này.**

**2. Telemetry tần suất cao.** Nhiệt độ máy sấy lấy mẫu mỗi 100 ms, đường cong formation của
từng cell. Volume lớn, ghi nhiều đọc ít, và phải giữ nhiều năm cho hồ sơ tuân thủ. Cần
compression, downsampling và retention policy như một tính năng của engine — không phải như một
cron job tự viết.

## Decision

Dùng **PostgreSQL 17 + TimescaleDB 2.29.2** (`timescale/timescaledb:2.29.2-pg17`) cho:

- **read model** — WIP board, OEE, dashboard; JSONB cho projection linh hoạt;
- **genealogy closure table** — `numrange` + GiST + `EXCLUDE` constraint;
- **telemetry** — hypertable `ts.process_signal`, `chunk_time_interval = 1 day`, bật
  `timescaledb.compress`, có continuous aggregate và retention policy.

Binary (ảnh vision, raw curve CSV, log máy) **không** nằm ở đây: MinIO giữ file, DB chỉ giữ key
+ SHA-256. Không bao giờ blob trong DB giao dịch.

## Consequences

**Được**

- Truy vấn overlap dùng được index thật: `g.span && numrange(1250, 1430)` với GiST.
- `EXCLUDE` constraint biến "dữ liệu chồng lấn" từ bug tiềm ẩn thành lỗi ghi — phát hiện tại chỗ,
  không phải ba tháng sau lúc audit.
- Hypertable + compression + retention là tính năng của engine, không phải code tự viết cần bảo
  trì.
- Đọc nặng tách khỏi write → dashboard không tranh chấp khoá với đường ghi event.
- JSONB cho phép đổi hình dạng projection mà không cần migration mỗi lần.

**Mất / phải chịu**

- **Polyglot persistence.** Hai engine, hai bộ migration (EF Core ↔ SQL thuần), hai connection
  string, hai backup, hai bộ kỹ năng vận hành.
- **Projection không cùng transaction với write.** Ghi SQL Server xong, projection sang
  PostgreSQL là bước riêng. Bắt buộc: projection **idempotent** và có **checkpoint** để replay.
  Không có đường tắt nào ở đây.
- Read model **eventually consistent** với write model. Mọi màn hình đọc từ read model phải chịu
  được việc dữ liệu trễ vài trăm ms, và UI không được hứa điều ngược lại.
- Thêm 1 GB `mem_limit` vào ngân sách 6 GB.
- Test tích hợp cần Testcontainers cho **cả hai** engine → chậm hơn.

**Việc phát sinh**

- M3: script migration TimescaleDB versioned, tách khỏi EF Core migrations.
- M5: mọi projection có bảng checkpoint + test replay từ đầu stream.
- M5: test chứng minh `EXCLUDE` constraint thật sự chặn được bản ghi chồng lấn.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **Read model ở SQL Server** (một engine duy nhất) | Rẻ nhất về vận hành, và đây là mặc định đúng nếu không có bài toán khoảng mét. Loại vì không có `numrange`, không có GiST cho overlap, không có `EXCLUDE` constraint. Thay thế bằng hai cột + B-tree là chấp nhận một truy vấn không index được ở đúng chỗ nóng nhất. |
| **PostgreSQL cho tất cả**, bỏ SQL Server | Xoá được toàn bộ mục "Mất / phải chịu" ở trên. Loại vì lệch stack Opcenter — xem [ADR-001](ADR-001-sql-server-event-store.md). |
| **InfluxDB / Prometheus cho telemetry** | Thêm engine thứ ba. Telemetry cần join với read model quan hệ (cell nào, thiết bị nào, ca nào); giữ chung PostgreSQL thì join được bằng SQL thường. |
| **Tự viết partition + cron nén** trên PostgreSQL thuần | Đúng thứ TimescaleDB đã làm sẵn và làm tốt hơn. Tự viết là mua thêm việc bảo trì để đổi lấy một dependency ít đi. |

## Evidence

- Image đang chạy: `docker-compose.yml` — `timescale/timescaledb:2.29.2-pg17`, `mem_limit: 1g`.
- DDL bảng cạnh genealogy và truy vấn overlap mẫu: `docs/scope.md` **§6.4**. Lý do chọn
  `numrange` + GiST thay vì hai cột `from_meter`/`to_meter`: cùng mục, khối *tip*.
- Read model: `docs/scope.md` §8.2. Hypertable `ts.process_signal`: §8.3.

Chưa có phép đo hiệu năng nào trong repo này — lựa chọn dựa trên tính chất của index chứ không
phải benchmark. Số thật sẽ vào `docs/benchmarks.md` ở **M5**, khi closure table có dữ liệu.
Nếu lúc đó recursive CTE + B-tree đạt SLO, ADR-006 phải nói lại chuyện đó thay vì im lặng.
