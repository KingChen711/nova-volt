# NovaVolt v2 — tracker

Trạng thái R0–R13 theo [scope v2](../scope.md). Mỗi mốc chỉ đổi sang **đạt** khi có code đang chạy,
test và số đo tương ứng (AGENTS.md §1.3). Agent làm theo lô lớn, không lập plan theo từng commit (§0.2).

| Mốc | Trạng thái | Bằng chứng / còn thiếu |
|---|---|---|
| R0 Đóng v1, khung v2, clean-room | **đạt** (2026-10-27) | Tag `r0`; mục R0 bên dưới |
| R1 Foundation: model, command, transaction | **đạt** (2026-10-27) | Tag `r1`; mục R1 bên dưới |
| R2 Foundation: App/OData, event, rule, key, state machine; khung web | **đạt** (2026-10-28) | Tag `r2`; mục R2 bên dưới |
| R3 Product | **đạt** (2026-10-28) | Tag `r3`; mục R3 bên dưới |
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

## R2

- App và extension app khai trong model (`AppDefinition`, POM), tiền tố `<ExtApp>_` dưới base app, extension app phẳng.
- Service layer HTTP (`src/Foundation/Nvm.Foundation.Web`): OData theo app (tập con, ADR-055), POST command theo tên
  công bố, reading function trả query trì hoãn, token JWT có hạn (hết hạn → 401 `6401`).
- Event lưu cùng transaction rồi lên RabbitMQ (exchange theo plant, queue theo worker role), relay gửi lại;
  subscription Predefined và theo plant; signal rule theo timer và theo event; configuration key; state machine dạng
  dữ liệu với role theo transition. Quyết định ở ADR-055.
- DoD: 26 test hành vi mới trong `tests/Integration/Nvm.FoundationTests` (tên ở [bảng cơ chế](../oef-mapping.md)),
  gồm lab L04, L05, và sớm L06, L15.
- Host chạy được (`src/Solution/Nvm.Mom.Host`, `make update-db`, `make mom`); realm Keycloak v2 (role theo khu, token
  600 giây, mật khẩu user thử từ `.env`), đóng N-R0-2.
- Khung web Next.js (`web/`, thiết kế ở `web/DESIGN.md`): đăng nhập Keycloak bằng PKCE, menu và route theo role, màn
  Quản trị (configuration key, state machine, signal rule và timer) hiện nguyên thân response. Kiểm tay trên trình
  duyệt 2026-10-28: `admin.nv1` thấy mọi khu, đọc và ghi key, thấy lỗi `7001` nguyên văn; `op.nv1` chỉ thấy hai khu và
  bị chặn ở `/admin`. Job CI `Web` (kiểm kiểu, build).
- Cổng web đổi 3000 → 3100: Windows trên máy giữ riêng dải 3000–3019. Next ghim 16.4.3 (vá ba advisory của ≤ 16.4.0).

## R3

- Lớp Product (`src/Product/Nvm.Product`): ba FB `FB_MS_Product`, `FB_OP_Product`, `FB_RF_Product`; base app
  `Production` công bố 27 entity, 43 command. Equipment tám cấp (property, nhóm), material và revision, BoP/process/
  operation (máy được phép, định mức), MTU và vật chứa lồng nhiều tầng, lịch sử MTU chỉ thêm dòng, WO từ process với
  trạng thái theo dữ liệu (`InstallWorkOrderStates`, ép trạng thái, hủy theo role), ghi sản phẩm, tiến độ bước, trừ
  nguyên liệu (genealogy), NC, giữ và thả danh sách MTU, containment, buffer, yêu cầu vận chuyển. Quyết định ở ADR-056;
  hành vi, nguồn và giả định ở [bảng cơ chế](../oef-mapping.md#lớp-product).
- DoD: 30 test hành vi trong `tests/Integration/Nvm.ProductTests` (SQL Server thật). Mọi command có test
  (`Every_published_command_is_exercised_by_a_test`); mọi entity đọc được qua đường OData
  (`Every_entity_set_of_the_production_app_is_readable`); lịch sử MTU ghi Create, Assemble, Edit
  (`History_records_create_assemble_and_edit_in_order`).
- Host nạp lớp Product cùng Foundation. R3 không có màn web (màn đi cùng luồng từ R4).
- Web: thanh cuộn custom toàn cục (thumb mảnh theo token; sidebar tối có màu riêng).

## Nợ mở

| Mã | Nợ | Mốc xử lý |
|---|---|---|
| N-R0-3 | Observability stack chưa có trong compose v2. Trace hiện đi ra log của host và `ActivitySource`; dashboard theo `CorrelationId` thuộc bộ chẩn đoán | R12 |
| N-R0-4 | Site tài liệu Mintlify vẫn mô tả v1; chưa chạy kiểm clean-room | R12 |
| N-R2-1 | Service layer chưa kiểm quyền theo role cho từng command: ai có token hợp lệ cũng gọi được mọi command app công bố. Web chỉ ẩn menu và chặn route | R4 (trước khi có màn nghiệp vụ đầu tiên) |
| N-R2-2 | Web chưa có test tự động; R2 kiểm tay trên trình duyệt. Lab Playwright theo scope | R4 |
| N-R2-3 | Host chạy bằng `dotnet run`, chưa có trong compose | R12 |
