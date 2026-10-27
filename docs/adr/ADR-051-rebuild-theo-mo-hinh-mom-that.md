# ADR-051 — Dựng lại NovaVolt (v2) theo cơ chế và nghiệp vụ của một hệ MOM thật

| | |
|---|---|
| **Status** | **Accepted** — chủ repo duyệt 2026-10-27 |
| **Date** | 2026-10-27 |
| **Liên quan** | ADR-039 · ADR-052 · ADR-053 · [scope v2](../scope.md) · [tracker v2](../plans/tracker.md) · tag `v1.0` |

## Context

v1 (M0–M13) được thiết kế khi chủ repo chưa biết dự án sắp làm dùng nền tảng gì, nên nó "trung tính":
event sourcing cho từng đơn vị sản phẩm, genealogy DAG với closure table, MQTT Sparkplug B, TimescaleDB,
saga formation, grading/matching CP-SAT, DPP, B2MML, giao diện Mendix. Scope v1 §16 đã dự liệu bước
chỉnh khi biết câu trả lời.

Tới 2026-10-27 chủ repo đã tìm hiểu kỹ một hệ MOM thật chạy trên nền kiểu Opcenter EX: app sản phẩm
của nhà cung cấp được mở rộng bằng extension app, functional block, pre-check, post-action; tin máy
đi từ một Postgres phía xưởng qua bảng trung gian có trạng thái xử lý; tích hợp qua một gateway; vận
hành bằng gói deploy, suite kiểm thử dạng dữ liệu và quy trình xin thay đổi. Phần lớn những gì v1
dựng không có trong hệ đó, và những cơ chế hệ đó dựa vào (transaction của composite command không
nguyên tử, pre-check bật theo app, event không có subscription thì không ai nhận, xóa mềm) v1 không có.

Trạng thái v1 lúc quyết: backend M5–M12 đạt theo tracker; M4 Mendix và M13 còn mở (soak 24 giờ dừng
sau khoảng 8 giờ theo chủ repo; N1/N2 bị chặn bởi phần cứng; màn Mendix hoãn từ 2026-09-28).

## Decision

Dựng lại NovaVolt thành v2: bản mô phỏng tại nhà của hệ MOM thật, gồm bốn lớp Foundation (cơ chế
nền), Product (vai nhà cung cấp sản phẩm), Project (vai team dự án), Solution (gói, cấu hình theo
plant, deploy). Cơ chế nền tái hiện **theo hành vi đã quan sát** trên hệ thật; nghiệp vụ đồng dạng
nhưng đổi tên theo ADR-052. Lộ trình R0–R13 ở [scope v2](../scope.md).

v1 đóng tại hiện trạng ở tag `v1.0`. Các gate còn mở của v1 (soak 24 giờ, N1/N2, màn Mendix M4–M12,
Mendix cloud) chuyển thành **không làm, đã thay**: chúng đo những thành phần v2 không mang sang. Code
v1 xóa khỏi `main`; phần dùng lại được (lịch sản xuất, hosting, observability, analyzer) giữ lại.
ADR 001–050 giữ nguyên nội dung; ADR nào gắn với phần đã bỏ được đánh dấu ở index.

## Consequences

**Được**

- Luyện đúng cơ chế và tình huống của công việc thật, ở máy cá nhân, không cần VM dùng chung.
- Mỗi hành vi có nguồn quan sát và test mang tên hành vi đó, thay vì suy từ đề cương khóa học.

**Mất / phải chịu**

- Bỏ phần lớn code v1 đã kiểm chứng (event store, outbox, projection, ingestion Sparkplug, telemetry,
  grading, passport). Muốn xem lại phải checkout tag `v1.0`.
- Các bằng chứng hiệu năng của v1 (`benchmarks.md`) không còn áp dụng; v2 đo lại từ đầu.
- Không còn chứng minh được các mục tiêu v1 như forward trace closure table hay CP-SAT trên v2.
- Mô phỏng chỉ đúng tới mức hiểu biết hiện có về hệ thật; chỗ chưa rõ phải làm hai biến thể.

**Việc phát sinh**

- Viết lại AGENTS.md, README, scope, glossary, bảng cơ chế, tracker; ADR-052 (clean-room), ADR-053 (giao diện).
- Dựng lại khung solution và test kiến trúc theo bốn lớp.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Giữ nền v1, chỉ thay nghiệp vụ | Ít việc hơn nhưng không có pre-check theo app, composite không nguyên tử, subscription, xóa mềm: đúng những thứ sự cố thật xoay quanh |
| Làm tiếp v1 cho đủ gate (soak 24 giờ, N1/N2, Mendix) rồi mới dựng lại | Tốn máy và công cho thành phần sắp bỏ; chủ repo chọn đóng tại hiện trạng |
| Repo mới | Mất lịch sử, CI, cấu hình; phiên làm việc và memory gắn với đường dẫn repo này |

## Evidence

Quyết định dựa trên phán đoán và tài liệu tìm hiểu riêng của chủ repo (không đưa vào repo theo
ADR-052), không dựa trên phép đo. Trạng thái v1 lúc đóng: tag `v1.0` (`663baf9`).
