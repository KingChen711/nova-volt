# NovaVolt v2 — tracker

Trạng thái R0–R13 theo [scope v2](../scope.md). Mỗi mốc chỉ đổi sang **đạt** khi có code đang chạy,
test và số đo tương ứng (AGENTS.md §1.3). Agent làm theo lô lớn, không lập plan theo từng commit (§0.2).

| Mốc | Trạng thái | Bằng chứng / còn thiếu |
|---|---|---|
| R0 Đóng v1, khung v2, clean-room | **đạt** (2026-10-27) | Tag `r0`; mục R0 bên dưới |
| R1 Foundation: model, command, transaction | **đạt** (2026-10-27) | Tag `r1`; mục R1 bên dưới |
| R2 Foundation: App/OData, event, rule, key, state machine; khung web | chưa làm | |
| R3 Product | chưa làm | |
| R4 Runner suite, master data, WO theo ca | chưa làm | |
| R5 Tin từ xưởng | chưa làm | |
| R6 Hàng lỗi | chưa làm | |
| R7 Đóng gói, cất kho, trailer kho, kiểm pallet | chưa làm | |
| R8 Xuất hàng | chưa làm | |
| R9 Xe linh kiện, bãi, dock, skid | chưa làm | |
| R10 Kanban, gọi hàng, bảng gọi hàng, nạp nguyên liệu | chưa làm | |
| R11 Gateway, hệ ngoài giả lập | chưa làm | |
| R12 Vận hành kiểu dự án | chưa làm | |
| R13 Hoàn thiện web | chưa làm | |

## R0

- v1 đóng ở tag `v1.0` (`663baf9`), đã đẩy lên remote. Gate còn mở của v1 chuyển thành "không làm, đã thay" (ADR-051).
- ADR-051 (dựng lại), ADR-052 (clean-room, K14), ADR-053 (Next.js).
- Kiểm clean-room: `tools/cleanroom/check.py`, HMAC trong `tools/cleanroom/denylist.hmac`, khóa ở secret
  `NVM_CLEANROOM_KEY`; chạy ở pre-commit, `make ci`, job CI `Clean-room`. Thử với một file chứa từ cấm ghép
  camelCase: bị chặn, báo đúng `file:dòng`.
- Khung solution bốn lớp + `Shopfloor`, `Integration`; test kiến trúc đọc đồ thị `ProjectReference`
  (có đối chứng dương). Giữ từ v1: `Nvm.Time`, `Nvm.Hosting`, `Nvm.Observability(.Web)`, analyzer NVM001–003.
- Hạ tầng rút gọn: SQL Server (SQL Agent bật), RabbitMQ, Keycloak, LineDB (Postgres 17), Redpanda, Mailpit.
- Kiểm chứng trên máy cá nhân: format sạch, build Release 0 lỗi, test 83/83 xanh; `docker compose config` hợp lệ.

## R1

- Foundation (`src/Foundation/Nvm.Foundation`): model khai bằng attribute, `ModelRegistry`, Update Database
  (`SchemaUpdater`), pipeline kiểm model → pre-check theo app → handler → post-action → commit → phát event,
  transaction theo domain, composite, xóa mềm số đếm, facet, trace `CorrelationId`/`CurrentId`/`ParentId`/`RootRequestId`.
  Quyết định ở ADR-054; giả định chưa xác minh ở [bảng cơ chế](../oef-mapping.md).
- DoD 1–8: test hành vi trong `tests/Integration/Nvm.FoundationTests` (SQL Server thật, Testcontainers), tên test
  ghi ở bảng cơ chế. 27/27 xanh.
- DoD 9: 1.000 command tuần tự, p95 14,7 ms (ba lần đo, [benchmarks](../benchmarks.md)); test giữ ngưỡng p95 < 50 ms.
- NVM004 chặn `Query<T>` khác domain lúc build; NVM002/NVM003 trỏ sang kiểu của Foundation.
- `System.Security.Cryptography.Pkcs` ghim 10.0.14 (vá GHSA-846f-f764-76mx), đóng N-R0-5.
- `make up` của v2 chạy thật: 6 service healthy, RAM rảnh 1.676 MiB trên quota 8 GB ([benchmarks](../benchmarks.md)),
  đóng N-R0-1. Cổng SMTP host của Mailpit đổi 1025 → 25025: trên máy có dải cổng động Windows bắt đầu từ 1024,
  1025 bị tiến trình hệ thống giữ.

## Nợ mở

| Mã | Nợ | Mốc xử lý |
|---|---|---|
| N-R0-2 | Realm Keycloak vẫn là bản v1 (role, user của v1) | R2 |
| N-R0-3 | Observability stack chưa có trong compose v2 | R2 |
| N-R0-4 | Site tài liệu Mintlify vẫn mô tả v1; chưa chạy kiểm clean-room | R12 |
