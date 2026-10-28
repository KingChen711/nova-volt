# NovaVolt v2 — tracker

Trạng thái R0–R13 theo [scope v2](../scope.md). Mỗi mốc chỉ đổi sang **đạt** khi có code đang chạy,
test và số đo tương ứng (AGENTS.md §1.3). Agent làm theo lô lớn, không lập plan theo từng commit (§0.2).

| Mốc | Trạng thái | Bằng chứng / còn thiếu |
|---|---|---|
| R0 Đóng v1, khung v2, clean-room | **đạt** (2026-10-27) | Tag `r0`; mục R0 bên dưới |
| R1 Foundation: model, command, transaction | **đạt** (2026-10-27) | Tag `r1`; mục R1 bên dưới |
| R2 Foundation: App/OData, event, rule, key, state machine; khung web | **đạt** (2026-10-28) | Tag `r2`; mục R2 bên dưới |
| R3 Product | **đạt** (2026-10-28) | Tag `r3`; mục R3 bên dưới |
| R4 Runner suite, master data, WO theo ca | **đạt** (2026-10-28) | Tag `r4`; mục R4 bên dưới |
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

## R4

- Foundation: extension app công bố POM của nó dưới base app (`<ExtApp>_<Command>`); quyền gọi command theo role ở
  service layer (`RestrictCommands`, role quản trị, HTTP 403 `6403`), đóng N-R2-1.
- Lớp Project (`src/Project/Nvm.Project`): extension app `NvProductionExt` trên `Production`; PS, DWO, facet lịch trên
  process (tiền tố tên WO, `MinimumFreezeHours`) và trên WO (PS, giờ đóng băng); timer đầu ca `DAILY_WO_<PS>` gọi
  `NvProductionExt_HandleShiftTimer`; năm pre-check: role theo transition cho Abort, Release, đổi trạng thái; chặn hai WO
  Active cùng máy; chặn xóa WO đã chạy.
- Solution (`src/Solution/Nvm.Solution`): manifest dùng chung cho host và test; runner suite kiểu Tester Tool (Command,
  Read, Function, Sql; CSV, `#GET->…#`, `ExpectedSuccess`, `IfThisSkipNext`, assert); bước deploy `make master-data`.
  Lớp Product thêm `UpdateMaterial`.
- Master data NV1 (`suites/`): 98 equipment (2 xưởng, 4 line, 16 khu, yard, dock, lưới sàn), 14 material hai họ, 16
  process, 32 PS, 32 DWO, vòng đời WO của team (Frozen, role cho Frozen → Aborted như hệ thật). Quyết định ở ADR-057.
- DoD: 16 test trong `tests/Integration/Nvm.SolutionTests`: nạp từ DB trống, chạy lại không tạo trùng; 3 ngày ca bằng giờ
  giả (16 WO mỗi ca, ca trước Frozen, Complete đúng 24 giờ, Formation chưa Complete); hai ngày đổi giờ; lab L06, L07 (+đối
  chứng), L08 (hai biến thể), L16. Deploy thật trên stack local: Update Database, 9/9 suite Executed, chạy lại 9/9 Skipped.
- Web: màn "Lệnh theo ca" ở khu Xưởng (`/area/line`): ca và timer, DWO, WO theo khu; hủy và tạm dừng hiện nguyên thân lỗi.
  Lab giao diện Playwright (`make lab-web`, Edge) cho N-R2-2.
- Kiểm trên stack local 2026-10-28 (host bản R4): gọi `NvProductionExt_HandleShiftTimer` bằng tay dưới `lead.nv1` cho
  16 khu → 16 WO Active ca S2; `op.nv1` gọi cùng lệnh → 403 `6403`. Màn `/area/line` sáng và tối đúng dữ liệu.
  `make lab-web` 3/3 xanh, L06 chạy thật (WO đóng băng tay, hủy bị pre-check chặn, màn hiện HTTP 400 nguyên văn).
  Lab tìm ra: nhãn ô chọn khu trùng `nav` "Khu" của sidebar (đổi thành "Khu sản xuất"); spec đếm WO Frozen trước khi
  bảng nạp xong; DB local còn role `line-lead` trên Frozen → Aborted từ lần kiểm màn Quản trị ở R2, làm lead hủy được
  (đã xóa mềm dòng đó; nền chưa có lệnh gỡ role theo transition).
- Benchmark 1.000 command (DoD R1) chạy riêng sau lượt test chung, ở cả `make ci` và CI: chạy chung với ba bộ test tích
  hợp thì p95 vượt 50 ms vì tranh CPU (53 ms hai lần), chạy riêng thì qua.

## Nợ mở

| Mã | Nợ | Mốc xử lý |
|---|---|---|
| N-R0-3 | Observability stack chưa có trong compose v2. Trace hiện đi ra log của host và `ActivitySource`; dashboard theo `CorrelationId` thuộc bộ chẩn đoán | R12 |
| N-R0-4 | Site tài liệu Mintlify vẫn mô tả v1; chưa chạy kiểm clean-room | R12 |
| N-R2-2 | Lab giao diện Playwright có từ R4 (`make lab-web`) nhưng cần cả stack chạy nên chưa vào CI | R12 |
| N-R2-3 | Host chạy bằng `dotnet run`, chưa có trong compose | R12 |
