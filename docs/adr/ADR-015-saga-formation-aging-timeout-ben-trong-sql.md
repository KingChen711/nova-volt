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
- Không dùng RabbitMQ delayed exchange, đúng scope. Lab #1 đã chạy (2026-09-27, xem mục Lab M7 #1 bên dưới): plugin
  giữ timeout trong RAM broker, không cho xem/huỷ timeout đang chờ, và bắn ngay một delay quá 49,7 ngày.

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

## Lab M7 #1 — RabbitMQ delayed message plugin (2026-09-27, Claude)

`DelayedExchangeLabTests` (NVM_RUN_LABS=1). Plugin `rabbitmq_delayed_message_exchange-4.2.0.ez` (GitHub release
rabbitmq/rabbitmq-delayed-message-exchange v4.2.0, SHA-256
`f168b2c09810cde3726961d31f38e3408e6a7fbff3929908d6f962061d8e70a1`, không nằm trong git). File `.app` của plugin đòi
`broker_version_requirements = ["4.2.0"]`, nên **không chạy được trên RabbitMQ 4.3.5 của runtime**; lab dùng
`rabbitmq:4.2.9-management`. Một lần chạy, Windows 11 + Docker Desktop:

| Phép đo | Kết quả |
|---|---|
| Publish 30.000 timeout 10 ngày (publisher confirm) | 28,2 s |
| Bộ nhớ node trước / sau | 222,9 MB / 260,8 MB (+37,9 MB, ~1,3 KB mỗi timeout) |
| Bộ nhớ sau khi restart broker | 257,5 MB (timeout được nạp lại vào RAM) |
| Timeout đang chờ hiện trong queue | 0 — không liệt kê, không huỷ được qua queue |
| `x-delay` = 50 ngày (> 2³² − 1 ms) | **tới queue ngay trong 5 s**, không lỗi |
| `x-delay` = 2 s | tới đúng hạn |
| `x-delay` = 30 s, restart broker giữa chừng | vẫn tới sau khi broker lên lại |

Kết luận so với cách hiện tại (bảng `execution.ProcessTimeouts` trong SQL):

- Mỗi timeout chiếm RAM broker suốt thời gian chờ. Với aging 10 ngày, số timeout bằng số cell đang aging; đo được
  ~1,3 KB/timeout, tức vài trăm MB khi lên vài trăm nghìn cell, trên đúng broker chở luồng event realtime. Số bộ nhớ là
  chênh lệch `mem_used` của cả node, một lần chạy, không có nhóm đối chứng: xem như cỡ độ lớn, không phải hằng số.
- Không xem được timeout nào đang chờ, không huỷ được khi cell đổi trạng thái; handler phải tự bỏ qua timeout hết
  hiệu lực. Bảng SQL cho cả hai, và `FormationReconciliation` dựa vào việc đọc được hạn đã lưu.
- Delay dài hơn ~49,7 ngày không bị từ chối mà bắn ngay: một cấu hình aging dài hơn sẽ âm thầm bắn timeout sớm.
- Plugin khoá phiên bản broker theo từng bản phụ (4.2.0 chỉ cho 4.2.x): nâng RabbitMQ phải chờ plugin.

Quyết định giữ nguyên: timeout ở SQL.
