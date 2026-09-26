# ADR-015 — Saga formation/aging: process manager tự viết, timeout bền trong SQL, đồng hồ là TimeProvider

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-26 |
| **Liên quan** | `docs/scope.md` §9/M7 · AGENTS.md K1 · ADR-022 · ADR-044 |

---

## Context

Formation mất tới 36 giờ, aging 10 ngày. Scope M7 gợi ý MassTransit state machine với Quartz.NET +
Postgres job store, và cấm RabbitMQ delayed exchange. DoD bắt buộc: tua 12 ngày bằng `FakeTimeProvider`
trong < 2 s (T5), restart giữa lúc chờ không mất timeout, 30.000 cell cùng ở Aging.

K1 yêu cầu mọi đồng hồ đi qua `TimeProvider`. Repo chưa dùng Quartz. Chưa kiểm cách Quartz hoặc scheduler
của MassTransit 8 nhận một `TimeProvider` ảo; không có bằng chứng thì T5 phải dựa vào thứ repo kiểm được.

## Decision

- Saga là **process manager** trong FB ProductionExecution (`FormationAgingProcessor`): mỗi cell một dòng
  `execution.FormationAging` (PK = một saga cho một cell), event trên stream `formation:{serial}`.
- Timeout là dòng `execution.ProcessTimeouts` ghi **cùng transaction** với trạng thái và event. Hạn nằm trong
  DB, không trong RAM hay broker.
- `FormationTimeoutWorker` đọc hạn đến theo `TimeProvider.GetUtcNow()` và bắn mỗi timeout bằng một command
  có idempotency key tất định (serial, loại, hạn). Timeout không còn áp dụng (trạng thái đã đi tiếp) chỉ được
  đánh dấu xong. Bắn song song tối đa 8 để một đợt tới hạn lớn không chiếm hết connection.
- `FormationReconciliation` tìm quá trình đang chờ mà mất timeout (lập lại từ hạn đã lưu) và stream còn event
  nhưng mất trạng thái (báo cáo cho người xử lý).
- Không dùng RabbitMQ delayed exchange, đúng scope. **Chưa chạy** lab #1 (đo bộ nhớ broker và giới hạn delay
  của plugin): image `rabbitmq:4.3.5-management` không có plugin và tải plugin cần duyệt riêng.

## Số đo (2026-09-26, Testcontainers SQL Server 2022, Docker 16 CPU / 8 GB)

- T5: saga đi hết vòng qua **12,04 ngày ảo trong 563 ms** (`FormationAgingProcessTests`), gồm restart host giữa
  lúc chờ, timeout formation → Faulted, drift 19 mV → Quarantined + NCR.
- 30.000 cell ở Aging (`FormationAgingScaleLabTests`, NVM_RUN_LABS=1): poll khi chưa có hạn đến p95 **0,95 ms**;
  truy vấn rack/level 100 dòng 67–72 ms (lần đầu, cache lạnh); xả 30.000 timeout cùng tới hạn: bắn tuần tự
  **54/s** (553 s, operator p95 28 ms), bắn song song 8 **301/s** (99,6 s, command serialize của operator chạy
  đồng thời p95 **39,3 ms**). Hai lần chạy, mỗi cấu hình một lần.
- Telemetry ingestion nằm ở PostgreSQL/Timescale, tách khỏi SQL Server của saga. **Chưa đo** ingestion đồng thời
  với 30.000 cell aging trên cùng máy.

## Consequences

**Được**

- Đồng hồ ảo là đồng hồ duy nhất của saga, nên T5 kiểm được trong một integration test thường.
- Mất trạng thái hay lịch hẹn được phát hiện bằng một truy vấn, không phải bằng một cell bị quên trong kho.

**Mất / phải chịu**

- Tự viết phần MassTransit/Quartz cho sẵn: poll, song song, đối chiếu. Độ trễ đánh thức tối đa bằng chu kỳ
  poll (5 s), chấp nhận được với hạn tính bằng giờ và ngày.
- Mỗi lần bắn là một transaction riêng; đợt tới hạn lớn xả theo tốc độ command, không phải tốc độ broker.
