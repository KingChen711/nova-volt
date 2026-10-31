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
| R5 Tin từ xưởng | **đạt** (2026-10-29) | Tag `r5`; mục R5 bên dưới |
| R6 Hàng lỗi | **đạt** (2026-10-29) | Tag `r6`; mục R6 bên dưới |
| R7 Đóng gói, cất kho, trailer kho, kiểm pallet | **đạt** (2026-10-29) | Tag `r7`; mục R7 bên dưới |
| R8 Xuất hàng | **đạt** (2026-10-30) | Tag `r8`; mục R8 bên dưới |
| R9 Xe linh kiện, bãi, dock, skid | **đạt** (2026-10-31) | Tag `r9`; mục R9 bên dưới |
| R10 Kanban, gọi hàng, bảng gọi hàng, nạp nguyên liệu | **đạt** (2026-10-31) | Tag `r10`; mục R10 bên dưới |
| R11 Gateway, hệ ngoài giả lập | chưa làm | |
| R12 Vận hành kiểu dự án | chưa làm | |
| R13 Hoàn thiện web | chưa làm | Giao diện đã đổi sang AWS Console dark theme (2026-10-30, `web/DESIGN.md`), nằm trong tag `r9` |

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

## R5

- LineDB (`src/Shopfloor/Nvm.Shopfloor/LineDb/Sql`, cài bằng `make linedb`): schema `elec`, `cell`, `form`, `mod` với bảng
  máy và trigger viết tay mỗi bảng, ba dạng JSON (`unit`, `batch`, `shrink`); bảng trung gian `relay.line_message`, trạng
  thái `NEW`/`SENT`/`DONE`/`NOTIFY_ERR`/`RETRY`/`FAILED` qua view `relay.message_status` với hai biến thể (cột theo môi
  trường, bảng lịch riêng); chính sách gửi lại theo cặp (trạng thái, mã lỗi); hàm `claim`, `write_result`, `send_pending`;
  role `nvm_mom` chỉ gọi được hai hàm trung gian (K11).
- Foundation: endpoint ngoài theo môi trường (`IPlatform.Endpoint`), adapter phát event theo tên (`RaiseExternalEvent`).
  Lớp Product: MTU có `Code` (serial, số lô).
- Lớp Project: `MapApp` (bảng đối chiếu bước, máy, nguyên liệu, lỗi; suite `MapApp_001_LineCodes`); `ApplyLineResults`
  (chọn bước WO, WO dự phòng trong khung ca, MTU sản phẩm, trừ nguyên liệu theo lô hoặc MTU tạm `Auto_…`, khóa
  `<serial>|<mã bước>` chống ghi hai lần); handler `LineMessageHandler` (worker role tuần tự) ghi kết quả về LineDB với mã lỗi
  ghép; đường poll `IngestLineMessages` + `SetLinePolling` (bước deploy `make line-polling`); đường relay là
  `LineRelayListenerService` trong host. Giả định ở [bảng cơ chế](../oef-mapping.md#r5-tin-từ-xưởng).
- Line simulator (`Nvm.Shopfloor.LineSimulator`): cả chuỗi điện cực → cell → formation → module cho một line, có KO, hao
  hụt, lô quét được hay không; trả số liệu MOM phải có.
- DoD đã có test (16 test mới, SQL Server và Postgres thật): 1 ca × 1 line khớp chính xác số MTU, dòng Assemble, số lượng WO
  (hai biến thể lưu trạng thái); gửi lại không trừ thêm; ngoài khung ca → `RETRY` −6200; relay đứng yên, poll gửi lại 1
  lần sau 10 phút; payload vượt giới hạn → `NOTIFY_ERR`; MOM dừng 30 phút rồi chạy lại: xả hết, không mất, không trùng (cả
  hai đường); lab L09, L10 (+ đối chứng), L11. Toàn bộ `make ci` trên máy cá nhân 2026-10-28: clean-room, format, build
  Release sạch, 204/204 test xanh.
- Simulator có CLI (`src/Shopfloor/Nvm.LineSimulator`, verb `run`, `relay`, `load`, `report`; target `make line-sim`,
  `line-relay`, `line-load`). Bước cài `make linedb` (biến `NVM_LINEDB_MOM_PASSWORD`, `NVM_LINEDB_STATE`, `NVM_ENVIRONMENT`
  trong `.env`).
- Kiểm trên stack local 2026-10-28: `make linedb` (biến thể bảng, môi trường qa), host nghe `line_messages`. Đường relay:
  52/52 dòng DONE, MOM có 52 MTU sản phẩm, 48 MTU `Auto_`, 124 dòng trừ nguyên liệu, 52 `LineExecution`, khớp kịch bản.
  Đường poll (`make line-polling`, line 3, 60 cell): 122/122 dòng DONE.
- Tải gấp đôi 60 phút trên đường poll: 5.833/5.833 dòng DONE, 0 lỗi, p95 66,1 s, tồn đọng 50–112 dòng không tăng, xả
  hết trong phút đầu sau khi ngừng sinh. Điều kiện và số ở [benchmarks](../benchmarks.md#r5--tin-từ-xưởng-tải-gấp-đôi).
- Web: màn "Tin từ xưởng" (`/area/line/messages`, tab cạnh "Lệnh theo ca"): serial đã ghi theo bước, lọc serial và tin
  KO, nguyên liệu đã trừ cho MTU sản phẩm, line-lead ghi tay một tin và thấy nguyên response. `make lab-web` 5/5 xanh (3
  của R4, 2 mới, gồm L09 trên giao diện).
- Kiểm lần cuối 2026-10-29 trên máy cá nhân: clean-room, format, build Release sạch, 204/204 test xanh, benchmark R1 chạy
  riêng xanh, `tsc` của web sạch.
- Lệch so với dự kiến: màn web không hiện trạng thái dòng LineDB (NEW, RETRY…), vì web chỉ đi qua OData của MOM (K10);
  xem trạng thái bằng `make line-load`/`report` hoặc SQL trên LineDB. Ghi ở nợ N-R5-3.

## R6

- Lớp Project (`src/Project/Nvm.Project`): danh mục chất lượng (scrap code, problem code, model code, cost center, lý do NC),
  nhà cung cấp theo material, cấu hình hủy theo (mã bước phía xưởng, mã trạng thái, ca, cờ chạy thử) với kết luận tự động,
  bước kết luận, lượng tách và mã báo scrap. Facet `NonConformanceDetail` trên NC của lớp Product, dấu chạy thử `MtuRunMode`,
  bộ đếm scrap tag và mã NCM/PCR.
- Tin máy: handler gọi tiếp `HandleLineNonConformances` sau `ApplyLineResults` (mã ghép loại 4) và `HandleLineShrink` cho tin
  hao hụt (loại 5, thay cho −5900 của R5). LineDB thay cờ `trial` bằng cột `run_class` (2 trial, 3 flow); bảng hao hụt có
  `loss_code`.
- Người dùng: Suspect/Hold, kiểm danh sách MTU, NC và hủy hàng loạt, kết luận SCRAP/USE AS IS, thêm MTU vào NC, khai NC theo bước
  WO, bước và lý do của NC, composite khai NC nhà cung cấp (lab L01), tháo rã có nhật ký và hoàn tác, containment request đổi
  tên theo mã NCM trong tên.
- Danh sách NC: reading model `NonConformanceListRow` trên view SQL, cài bằng `make views` (lab L15).
- Deploy: master data thêm defect, mã lỗi máy, danh mục chất lượng, nhà cung cấp, part được tháo; cấu hình hủy theo ca ở root
  riêng `suites/quality` (`make quality-config`). Lớp Product: tách được hết lượng của lô (lô mẹ còn 0).
- DoD: 26 luật viết lại từ suite chất lượng của team, đổi giá trị, ở `tests/Integration/Nvm.SolutionTests/Acceptance/Quality`,
  xanh (`QualityTests`). Lab L01, L12, L13, L14, L15 có test và đối chứng. Kiểm 2026-10-29 trên máy cá nhân: clean-room, format,
  build Release sạch, 215/215 test xanh, benchmark R1 chạy riêng xanh, `tsc` của web sạch, `make lab-web` 7/7 xanh.
- Kiểm trên stack local 2026-10-29: `make update-db`, `master-data`, `quality-config`, `views`, `linedb RESET=1`; đường poll,
  line 2 mô phỏng 24 cell (4 KO, 1 tin hao hụt): 47/47 dòng DONE sau bản sửa thứ tự tin bên dưới. Line 1 chạy trước bản sửa
  còn 1 dòng cuộn RETRY −4410: dữ liệu thử ghi sai lúc chưa sửa, gửi lại không sửa được vì bước ghi kết quả đã có.
- Tìm ra khi chạy trên stack local: đường poll đọc tin theo từng mã bước, nên tin cell tới trước tin cuộn; cuộn được tạo như
  lô tạm và giữ lượng 0 thay vì 120 m (lỗi có từ R5), rồi tách hao hụt hỏng −4410. Sửa: tin làm ra MTU tới sau thì đặt lượng =
  lượng của tin trừ phần đã trừ; có test.
- Web: màn "Kết luận NC" (`/area/quality`: danh sách theo trạng thái, kết luận SCRAP/USE AS IS, giữ hàng nghi lỗi hiện nguyên
  thân lỗi) và "NC hàng loạt" (`/area/bulk-nc`); lab Playwright `web/e2e/quality.spec.ts`.
- Chưa làm: màn Containment (khu `containment` vẫn là màn hẹn), in scrap tag, báo scrap ra hệ ngoài (thuộc R11).

## R7

- Lớp Product: vật chứa (`Mtua`) có state machine dạng dữ liệu, số lượng, material do người gọi đặt; lệnh `SetMtuaStatus`,
  `SetMtuaStatusForced`, `UpdateMtua`, `MoveMtua`, `DeleteMtua`; property của material. Quyết định ở ADR-058.
- Đóng gói (`Packing.cs`): bảng `PackingOperation` (đóng tote, xếp pallet, gỡ tote sau bước đo cuối); tin đóng tote của họ A ghi
  WO kit (`Kit_LN1/2`) rồi nạp kit vào tote, tote họ B và pallet chỉ đóng gói; post-action `UpdatePalletBuild` đếm tote, pallet,
  gói lẻ, ghi `BuildDate`, tháo hết tote khi gắn thẳng MTU vào pallet; dựng lại pallet sau bước đo cuối.
- `ShipApp` (`FinishedGoods.cs`): cất kho theo store address của part (`PartAddress` ở `MapApp`), post-action trên lệnh chuyển
  vật chứa kiểm bước đo cuối và hẹn event, subscriber ghi `FifoPallet`; kiểm pallet (cây 13 tầng, năm điều kiểm, QA Check),
  đề xuất FIFO, gỡ pallet, gói lẻ, timer hết hạn đo hằng ngày. Ba procedure và một index cài cùng bước SQL tay (`make views`);
  code gọi chúng qua kết nối riêng lấy từ key `Nv.SqlConnection` (`make sql-key`).
- `YardApp` (`StorageTrailers.cs`): trailer, lịch chất hàng của trailer kho, yêu cầu kéo xe; đặt và gỡ loại kho, gọi vào dock,
  nạp theo luật, Complete Load (chờ giao hoặc chờ đo), dỡ cả trailer, dỡ một pallet, kho chung khai bằng ghi chú.
- LineDB: bảng `mod.pack_result`, `mod.pallet_result`, `mod.eolv_result` và trigger; chính sách gửi lại (RETRY, −6500). Line
  simulator chạy tới tote, pallet, đo cuối (`make line-sim PACK=1 EOLV=1`).
- Master data thêm bằng suite mới (không sửa CSV của suite đã chạy): 31 equipment đóng gói, đo cuối, kho; material kit; 4
  process (`Kit`, `Eolv` của line 1–2) và 8 DWO; property `PalletQty`, `EolvRequired`; property `LocationType` của dock, yard;
  mã xưởng `mo020`, `mo025`, `mo030`, `eo010`; địa chỉ part; state machine của pallet, tote, gói lẻ, trailer kho, trailer; key
  gói lẻ và hết hạn đo; timer `EOLV_RETEST`; đường poll cho bốn mã bước mới (`line-polling/002`).
- Foundation: reading function ném lỗi lúc gọi trả HTTP 500 `ReadingFunctionFailed` kèm câu lỗi.
- DoD, kiểm 2026-10-29 trên máy cá nhân:
  - Chuỗi tin của line ra pallet Full đúng số tote: `PackingLineTests` (LineDB thật), họ A 8 tote, họ B 6 tote; tin xếp pallet
    tới trước tote thì RETRY −6500 rồi xong khi gửi lại; tin đo cuối gỡ tote, pallet về Empty.
  - Luật nạp trailer kho đủ ca đúng và sai: `StorageTrailerTests.Load_rules_hold_in_order` (9 ca) cùng 5 test vòng đời.
  - Kiểm NC đệ quy p95 636 / 701 / 758 ms (ba lần) trên dữ liệu 30 ngày mô phỏng (919 nghìn MTU, 2,23 triệu dòng genealogy); không có index thì
    một lần kiểm vượt 120 s ([benchmarks](../benchmarks.md#r7--kiểm-pallet-đệ-quy)).
  - Lab L03, L17, L18, lab cắt chuỗi ở procedure, lab dỡ pallet cuối: `FinishedGoodsTests`, `StorageTrailerTests`, có đối chứng.
  - `make ci` 2026-10-29: clean-room, format, build Release sạch, 249/249 test xanh, hai benchmark (R1, R7) chạy riêng xanh; `tsc` của web sạch.
  - Web: màn Cất kho, Kiểm pallet, Gỡ pallet và gói lẻ (`/area/finished-goods`), Trailer kho (`/area/yard`), Dựng lại pallet (`/area/line/rebuild`); lab `web/e2e/finished-goods.spec.ts` 3/3 xanh trên stack local (cần pallet mới từ `make line-sim PACK=1`), 7 spec cũ vẫn xanh. Lab bắt được lỗi mà test tích hợp (chạy bằng `sa`) không thấy: user ứng dụng thiếu quyền EXECUTE procedure; đã cấp trong `deploy/mssql/init/01-database.sql`.
- Kiểm trên stack local 2026-10-29: deploy tăng dần (suite cũ bỏ qua, 12 suite R7 chạy), `make views`, `make sql-key`,
  `make linedb RESET=1`; đường poll, line 3 (360 cell) và line 1 (192 cell) với `PACK=1`: 2 pallet Full.

## R8

- Foundation: gửi tin ra ngoài (`IPlatform.SendMessage` đồng bộ có timeout, `SendMessageAsync`), theo adapter, đi ngay lúc gọi; gateway
  gắn vào executor, mặc định không có gateway (gửi đồng bộ báo lỗi). Host: adapter file cho nhãn, email, ASN (`output/outbox`),
  adapter HTTP tới client gateway khi khai `NVM_GATEWAY_URL`. Lớp Product: MTU `Shipped`, lệnh `ShipMtu`. Quyết định ở ADR-059.
- `ShipApp` (`Shipping.cs`, `Loading.cs`): đơn bán JSON với Ack từng đơn, họ theo mã hãng vận tải, route họ A ghép từ nơi nhận và giờ
  giao, họ B từ route của khách hoặc dữ liệu đặt hàng (`CustomerBoxOrder`); nhóm đơn và chuyến giao; Release; picking FIFO (cờ lấy
  lệch, kiểm hàng giữ bằng procedure, đo cuối họ A, tuổi hàng họ B theo key `Nv.MinimumItemAgeHours`), bỏ pick, đóng pick list thiếu
  phải xác nhận; nhãn skid họ A (SkidTag) và họ B (SkidSheet hai mặt), nhãn hộp, vận đơn ra file XML; staging vào khu tập kết của
  nhóm route chính, email danh sách pallet; ngoại lệ theo thứ tự SkidLevel → SkidBuild → Trailer, mã tự khai; chất xe, họ B gửi
  SkidBuild rồi ShipLoad đồng bộ tới `CustomerShipSys`; ASN họ A sau commit, phản hồi ASN thành email.
- `YardApp`: xe của khách có route pool; nhận xe vào ô yard gắn chuyến, Move In, Move Out chỉ ra ô của nhóm route, yard pass `YP-…`,
  xe rời (đơn, nhóm Shipped; pallet Empty ở `n/a`; tote Empty rời pallet; hàng Shipped; xe Free).
- Master data bằng suite mới: khu tập kết A, B; nhóm route `KLN`, `ZR4K` (khu tập kết, ô yard); trạng thái xe `CustomerEmpty`,
  `CustomerLoaded`; suite gốc `ShipApp` (nơi nhận, danh mục 14 mã ngoại lệ). Quyền: role `shipping` cho xuất hàng, `yard`/`shipping`
  cho xe của khách; nhận đơn, dữ liệu đặt hàng, phản hồi ASN chỉ quản trị (tin từ hệ ngoài, R11).
- DoD, test ở `tests/Integration/Nvm.SolutionTests/ShipmentTests.cs`:
  - Hai họ chạy trọn tới Shipped: `Family_a_runs_from_sales_order_to_shipped_and_sends_the_asn`,
    `Family_b_needs_the_box_order_scans_every_box_and_ships_after_the_customer_system_confirms`.
  - Ma trận ngoại lệ 6 ca đúng nhóm, đúng thứ tự: `Loading_exceptions_are_asked_by_group_in_order_and_the_automatic_code_is_declared_without_asking`.
  - Hệ của khách không trả lời (cả trả thiếu số, cả không có gateway) → pallet giữ Staged, xe chưa chất:
    `Customer_system_not_replying_keeps_pallets_staged_and_the_trailer_unloaded`.
  - Thêm: luật picking (`Picking_blocks_…`), cơ chế gửi tin ở Foundation (`MessageBehaviorTests`, 3 test), `ShipMtu` ở lớp Product.
- `make ci` 2026-10-30 trên máy cá nhân: clean-room, format, build Release sạch (0 cảnh báo), 258/258 test xanh, hai benchmark (R1, R7)
  chạy riêng xanh.
- Test bắt được: mã skid họ B trùng giữa hai chuyến của cùng một đơn khách (giao một phần rồi giao tiếp); NId skid họ B gồm cả chuyến.
- Web: màn "Đơn và picking", "Staging và chất xe" (`/area/finished-goods/orders`, `/staging`), "Xe của khách" (`/area/yard/customer`).
- Kiểm trên stack local 2026-10-30: deploy tăng dần (6 suite R8 chạy, suite cũ bỏ qua), host ghi nhãn, email, ASN ra `output/outbox`.
  Lab `web/e2e/shipping.spec.ts` 5/5 xanh: họ A từ đơn ERP tới Shipped trên màn (cất kho, Release, picking FIFO, nhãn skid, staging
  từ chối pallet chưa lấy, xe sai route pool bị từ chối, chất xe, vận đơn, Move Out sai ô bị từ chối, yard pass, xe rời; pallet Empty ở
  `n/a`, ASN ra file); họ B tới Confirm, không có hệ của khách nên báo lỗi gửi SkidBuild, pallet giữ Staged. Ba spec R7 xanh khi có
  pallet mới từ simulator.
- Lab bắt được: (1) form chất xe hai ô không có nút submit nên Enter ở ô quét skid không thêm skid (đã thêm nút); (2) lỗi có từ R5–R6:
  trừ nguyên liệu theo mã lô chọn cả lô con hao hụt đã hủy (cùng mã với lô mẹ), làm cây thành phần của pallet có hàng Scrapped và
  picking chặn đúng luật. Sửa: bỏ lô đã hủy khi chọn; test trong `QualityTests.Shrink_rows_from_linedb_are_split_from_the_lot_and_scrapped`.
  Pallet tạo trước bản sửa trên stack local vẫn mang genealogy cũ.

## R9

- `YardApp` (`Inbound.cs`): lịch xe từ `SupplierPortal` (JSON, khóa route + giờ dự kiến), đăng ký xe linh kiện ở cổng, placard `PL-C`, `PL-E`, `PL-R`, `PL-G`;
  xe rỗng và gọi xe rỗng; năm loại xe không lịch (nguyên liệu đặt thẳng, loại ống, linh kiện đột xuất, xe rỗng của khách, thành phẩm trả về); đổi điểm thả, khu bãi của người
  kéo xe; nhận skid (nhãn 40 ký tự cắt theo vị trí, logical dock theo property của cửa, mã phụ của vị trí, vị trí mặc định từ part address, MTU tên ghép);
  dỡ xong (dỡ thiếu phải xác nhận, email skid chưa nhận); báo đã chất theo phần trăm; yard pass; xe rời (xe của hãng vận chuyển không cần yard pass, thành Deleted); chuyển skid giữa hai xe
  có lý do và lịch sử; nhà cung cấp tại chỗ tự nhận material trong danh sách.
- Master data bằng suite mới: 4 cửa dock, 6 ô yard, khu nhận hàng; trạng thái xe `FullReturned`, `Deleted`; lý do chuyển; nhà cung cấp tại chỗ.
- Khác hệ thật có chủ đích: lịch giữ skid trực tiếp, không có tầng đơn và nhà cung cấp riêng (đủ cho bốn luồng); giữ nguyên nghĩa cột và thứ tự kiểm.
- DoD, test ở `tests/Integration/Nvm.SolutionTests/InboundTrailerTests.cs` (13 test): bốn luồng trọn, trạng thái lịch và trailer khẳng định sau từng bước, gồm ca sai xe, sai vị trí, sai logical dock,
  vị trí khác mặc định, dỡ thiếu, phần trăm 0, placard sai route, thiếu yard pass, còn yêu cầu kéo, chuyển sang xe rỗng, chuyển một phần. Lab L19 có đối chứng dương; skid đã nhận không chuyển được.
- Kiểm 2026-10-30 trên máy cá nhân: clean-room, format, build Release sạch, 271/271 test xanh, hai benchmark chạy riêng xanh.
- Web: "Xe đến", "Điều xe", "Nhận skid", "Chuyển skid" (`/area/yard/inbound`, `/dock`, `/receive`, `/transfer`). Lab `web/e2e/inbound.spec.ts` 3/3 xanh trên stack local: đăng ký, kéo xe, nhận skid,
  nhãn lệch một ký tự, skid đã nhận không chuyển, chuyển rồi nhận trên xe đích, xe nguyên liệu đặt thẳng vào thẳng cửa rồi Deleted.

## R10

- Lớp Product: sửa yêu cầu vận chuyển theo quan sát (yêu cầu theo material và số lượng, việc chở riêng; `oef-mapping.md`), thêm `LinkMtuToBuffer` không xét whitelist.
- `PullApp` (`Pull.cs`): route bốn tier có phút từng chặng, whitelist bắt buộc ở mọi tier khi gọi; gọi hàng sinh cả yêu cầu lẫn việc chở (post-action); nhận việc, quét lô (nhãn skid, nhãn đặt thẳng; chỉ ra lô đang thuộc buffer, cờ đúng nguồn, FEFO), ghi lô, hoàn tất; post-action dời xuống chặng dưới, picklist gửi `WMS` khi lấy từ tier 1; hạn chặng; bảng gọi hàng tính theo dòng đang chọn; nhận đặt thẳng (quy đổi hệ `Receiving`, hạn dùng theo batch, chặn vượt, gắn buffer); nạp lô, gỡ lô.
- `MapApp`: kanban theo khóa part, dock, nhà cung cấp, ngày hiệu lực; part address có route và hiệu lực, màn lưu thì báo `SupplierPortal`; quy đổi đơn vị. Skid nhận xong gắn buffer của vị trí cất (R9).
- Master data bằng suite mới: ô tier, thuộc tính khu, buffer, whitelist, kanban, part address, route.
- DoD, test ở `tests/Integration/Nvm.SolutionTests/PullTests.cs`: chuỗi 4 → 3 → 2 rồi dừng, route trống tier 3, route chỉ có tier 4 (có yêu cầu, không có việc), ô không mã máy, kệ còn hàng vẫn bù, ma trận bảng gọi hàng, kanban hết hạn, nhập kanban sai ngày, nhận vượt, thiếu đơn, FEFO, sai part, sai nguồn, hủy, role. Lab L20 có đối chứng dương (gỡ rồi nạp được).
- Kiểm 2026-10-31 trên máy cá nhân: clean-room, format, build Release sạch; bộ test đầy đủ không gồm benchmark 297/298, ca đỏ do hai lô cùng giờ trên đồng hồ giả nên thứ tự FEFO không cố định, đã thêm tiêu chí phụ theo tên lô rồi chạy lại cả `PullTests` 26/26. Sau bản sửa này chưa chạy lại toàn bộ.
- Web: "Gọi hàng", "Nạp lô" ở khu Xưởng; "Bảng gọi hàng", "Việc chở", "Nhận đặt thẳng", "Kanban và route" ở khu Gọi hàng. Lab `web/e2e/pull.spec.ts` 2/2 xanh trên stack local.

## Nợ mở

| Mã | Nợ | Mốc xử lý |
|---|---|---|
| N-R0-3 | Observability stack chưa có trong compose v2. Trace hiện đi ra log của host và `ActivitySource`; dashboard theo `CorrelationId` thuộc bộ chẩn đoán | R12 |
| N-R0-4 | Site tài liệu Mintlify vẫn mô tả v1; chưa chạy kiểm clean-room | R12 |
| N-R2-2 | Lab giao diện Playwright có từ R4 (`make lab-web`) nhưng cần cả stack chạy nên chưa vào CI | R12 |
| N-R2-3 | Host chạy bằng `dotnet run`, chưa có trong compose | R12 |
| N-R5-1 | Line simulator 2 ngày × 4 line (tiêu chí "v2 xong") chưa chạy | R12 |
| N-R5-2 | Đường relay chưa đo tải; tải R5 chỉ đo đường poll | R12 |
| N-R5-3 | Trạng thái dòng LineDB chưa có trên web: cần một read model phía MOM hoặc API riêng của xưởng | R12 |
| N-R7-2 | Timer đầu ca chạy bù lúc host khởi động tranh ghi với WO dự phòng do tin máy mở cùng lúc: commit của timer hỏng, lần chạy đó mất, không thử lại (thấy trên stack local 2026-10-29, khu Module line 3) | R12 |
| N-R8-1 | Stack local không có hệ của khách: chất xe họ B dừng ở Confirm như trên máy ảo, trừ khi trỏ `NVM_GATEWAY_URL` tới hệ giả | R11 |
| N-R8-2 | Email (danh sách pallet, phản hồi ASN) ra file outbox, chưa qua SMTP | R11 |
| N-R8-3 | Ngoại lệ mức kiện khi một chuyến gồm nhiều nhóm đơn chưa dựng: một chuyến là một nhóm | R12 |
| N-R8-4 | Xe của khách rỗng đang gắn chuyến xuất hàng không có lệnh rời đi (nguồn không nói hệ thật làm gì); lab họ B để xe lại cửa dock, stack local phải dọn tay | R12 |
| N-R9-1 | Chưa có lịch sử di chuyển trailer, ghi chú nhiều dòng (chỉ `LastComment`), màn in placard, Components Unload Dashboard | R13 |
| N-R10-1 | Trạng thái picklist từ `WMS` về chưa xử lý (tự hoàn tất chặng); xe và lô nguyên liệu dạng ống chưa có nhánh riêng | R11 |
| N-R10-2 | Màn sửa part address chưa có (`SavePartAddress` đã có, màn hiện chỉ đọc); in nhãn địa chỉ chưa làm | R13 |
| N-R9-2 | Nhà cung cấp tại chỗ tự nhận: material thiếu địa chỉ kho thì để skid chưa nhận; hệ thật có thể báo lỗi cả đơn (chưa xác minh) | R11 |
| N-R8-5 | Lab `line-messages.spec` tìm dòng `ca010` trong 50 dòng mới nhất: chạy simulator có đóng gói ngay trước thì dòng đó bị đẩy ra, spec hỏng dù màn đúng | R12 |
| N-R7-3 | Đo cuối dựng bằng tin LineDB; màn phiên đo của hệ thật (kit không quét thành NC) chưa dựng | R13 |
