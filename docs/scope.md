# NovaVolt MOM v2 — Scope

> Hợp đồng gốc của v2 (ADR-051). AGENTS.md đứng trên tài liệu này. Mọi tên công ty, nhà máy,
> sản phẩm, hệ thống, mã số ở đây là **hư cấu** (ADR-052). v1 nằm ở tag `v1.0`.

## 1. Mục tiêu

NovaVolt v2 là bản mô phỏng tại nhà của một hệ **MOM** (Manufacturing Operations Management) chạy trên
nền kiểu Opcenter EX: cùng cơ chế nền, cùng hình dạng nghiệp vụ, cùng cách vận hành và chẩn đoán. Nó
dùng để luyện ba việc: kiểm thử quy trình nhà máy, xử lý sự cố (đọc thân lỗi, trace, dữ liệu, code),
và viết tính năng theo cách một team dự án mở rộng sản phẩm của nhà cung cấp.

1. Tái hiện cơ chế nền **theo hành vi đã quan sát** trên hệ thật (§4.2). Mỗi hành vi có một test mang tên hành vi đó.
2. Chạy trọn hai đường hàng: line → lệnh sản xuất theo ca → hàng lỗi → tote/pallet → kho → xuất hàng;
   xe linh kiện → nhận skid → kho → gọi hàng → line.
3. Tái hiện các sự cố điển hình bằng một lệnh và luyện đường chẩn đoán trên chúng (§6).
4. Vận hành như dự án: gói deploy, môi trường QA/STG/PROD, suite master data, quy trình xin thay đổi, dọn dữ liệu.
5. Giữ chất lượng kỹ thuật: observability, CI, test kiến trúc, lab phá hoại, số đo có ngày và commit.

**Không làm**: biến thể sản phẩm chưa chạy, hình dạng vật lý của máy, PLC thật, Opcenter thật; sao chép
code hay dữ liệu của bất kỳ dự án thật nào (ADR-052); event sourcing đơn vị sản phẩm, closure table,
MQTT/Sparkplug, TimescaleDB, saga formation, grading/matching, DPP, B2MML (đã có ở v1, không mang sang).

## 2. Nhà máy giả lập

- Doanh nghiệp `NOVAVOLT`, plant `NV1` làm module pin cho xe hybrid. Hai xưởng `B1`, `B2`; khu chung `CMN`
  (bãi xe). Bốn line, mỗi line bốn khu: điện cực (`ELEC`), lắp cell (`CELL`), formation (`FORM`), module (`MOD`).
- Cây thiết bị 8 cấp: Enterprise → Site → Plant → ProductionLine → Area → Subarea → WorkCenter → Unit.
  Mọi thứ là equipment, kể cả ô đặt hàng trên sàn, cửa dock, ô đỗ xe. Mã (`NId`) lặp lại đường đi trong cây.
- Hai họ thành phẩm theo khách:
  - **Họ A**: hai module ghép thành một kit, mỗi tote một kit, có bước đo cuối `EOLV` trước khi cất kho; khách nhận ASN.
  - **Họ B**: mỗi tote một module (hai biến thể theo số cell), nhãn hộp theo đơn của khách, chuyến xe phải được hệ của khách xác nhận.
- Hai ca mỗi ngày; giờ ca theo múi giờ của plant, lưu UTC.

## 3. Bốn lớp

| Lớp | Đóng vai | Nội dung |
|---|---|---|
| **Foundation** | Nền của nhà cung cấp | Khai báo model, command pipeline, transaction, event và subscription, OData theo app, reading function, signal rule, configuration key, state machine dạng dữ liệu |
| **Product** | Sản phẩm của nhà cung cấp | Base app `Production` gộp cả Equipment, Material, Defect (ADR-056): BoP, WO, MTU, NC, containment, buffer, logistic request |
| **Project** | Team dự án | Extension app `NvProductionExt` và FB của team; app nghiệp vụ `MapApp`, `YardApp`, `ShipApp`, `PullApp`; gateway |
| **Solution** | Cấu hình và deploy | Manifest: app, extension app, FB, command settings, subscription, worker role, signal rule; gói; deploy theo bước |

Phụ thuộc chỉ đi xuống: Project → Product → Foundation. Phía xưởng (`src/Shopfloor`) và gateway
(`src/Integration`) đóng vai hệ bên ngoài, chỉ thấy Foundation (K8, ép bằng test kiến trúc). Ở runtime,
command của extension app nằm dưới base app với tiền tố `NvProductionExt_`.

### 3.1 Cơ chế Foundation phải tái hiện

| Cơ chế | Hành vi phải có |
|---|---|
| Domain MasterData, OperationalData, ReferenceData | `Query` đọc cùng domain trong transaction; `ProjectionQuery` đọc cả domain khác, ngoài transaction, không thấy thay đổi chưa commit; analyzer chặn dùng nhầm model |
| Cột hệ thống, khóa logic | `Id` Guid, `NId`, `CreatedOn`/`LastUpdatedOn` UTC, phiên bản lạc quan; unique index `(IsDeleted, NId)`; ràng buộc bắt buộc ép ở tầng model |
| Xóa mềm | `Delete` đặt `IsDeleted` = số lớn nhất của bảng + 1, nên tạo lại cùng `NId` được; đọc qua platform tự lọc, SQL trực tiếp thì không |
| Facet | Mở rộng entity của lớp Product mà không sửa nó; mỗi entity một bảng facet dùng chung, cột sparse; phải import vào POM |
| Kiểm model ở service layer | Thiếu tham số bắt buộc → lỗi dạng `{"error":{"code":"6400"}}`, handler không chạy |
| Pre-check | Chỉ đọc; bật **theo app** (cùng command gọi qua app khác thì lọt); trượt → HTTP 400 có tên pre-check và chi tiết; cùng domain với command |
| Post-action | Đọc, ghi; chạy sau handler trong cùng transaction; áp cho mọi app; có thể tùy chọn và tắt; lỗi thì cả command lỗi |
| Transaction | Chuỗi `CallCommand` trong FB chung một transaction; composite command của app: mỗi lời gọi một transaction, lỗi giữa chừng để lại dữ liệu dở |
| Event | Phát sau commit; commit hỏng thì không phát; handler chạy bất đồng bộ, ngoài response |
| Subscription, worker role | Không có subscription thì event vẫn phát, không ai nhận, không lỗi; subscription khai trong model hoặc theo plant; worker role song song hoặc tuần tự, số instance |
| Reading function | Chỉ đọc, trả query trì hoãn qua OData; reading model là view SQL cùng tên |
| App, POM, OData | OData theo app; token có hạn (khoảng 10 phút), hết hạn → 401 |
| Extension app | Gắn vào một base app; không extend extension app; nhiều extension app cùng một base app |
| Signal rule, timer | Rule theo lịch hoặc theo event gọi command; timer tạo lúc chạy có mã định danh |
| Configuration key | `NId`, `Val` (chuỗi), `Type`, `Category`; mỗi chỗ đọc xử lý thiếu key theo cách riêng |
| State machine | Status và transition là dữ liệu; bảng role theo transition, transition không gán role thì cho qua |
| Dạng lỗi | Handler: `{"Error":{"ErrorCode","ErrorMessage"},"Succeeded":false}`; service layer: `error.code` |
| Trace | `CorrelationId` đi suốt chuỗi, kể cả command con và commit; có cha và gốc của lời gọi |
| Gửi tin ra ngoài | Đồng bộ (chờ, có timeout) và bất đồng bộ, kèm adapter và loại tin |

Bảng trạng thái từng cơ chế: [`oef-mapping.md`](oef-mapping.md).

## 4. Hạ tầng

| Thành phần | Vai |
|---|---|
| SQL Server (bật SQL Agent) | DB MOM của plant; job dọn dữ liệu |
| PostgreSQL | **LineDB** phía xưởng: schema theo khu, trigger, bảng trung gian chờ MOM đọc |
| RabbitMQ | Bus event của Foundation |
| Keycloak | Danh tính, role |
| Redpanda (Kafka API) | Gateway nhận lịch xe của nhà cung cấp và đơn của khách |
| Mailpit | Bắt email MOM gửi |
| OpenTelemetry, Tempo, Loki, Prometheus, Grafana | Trace theo `CorrelationId`, log, số đo (dựng cùng bộ chẩn đoán ở R12) |
| Next.js | Giao diện (ADR-053) |

## 5. Lộ trình

| Giai đoạn | Mốc | Cỡ |
|---|---|---|
| A. Nền | R0 đóng v1, khung v2, clean-room | S |
| | R1 Foundation: model, command pipeline, transaction | L |
| | R2 Foundation: App/OData, event, rule, key, state machine; khung web | L |
| | R3 Product: Equipment, Material, MTU, WO, NC, containment, buffer | L |
| B. Sản xuất trên line | R4 Runner suite, master data, WO theo ca | M |
| | R5 Tin từ xưởng: LineDB, relay và poll, ghi operation | L |
| | R6 Hàng lỗi: cấu hình hủy, chạy thử, hao hụt, giữ hàng, containment, tháo rã | L |
| C. Thành phẩm, hậu cần | R7 Đóng gói, cất kho, trailer kho, kiểm pallet | L |
| | R8 Xuất hàng: đơn bán, picking, staging, chất xe, ngoại lệ | L |
| | R9 Xe linh kiện, bãi, dock, nhận skid, chuyển skid | L |
| | R10 Kanban, gọi hàng theo tier, bảng gọi hàng, nạp nguyên liệu | M |
| D. Tích hợp, vận hành | R11 Gateway và hệ ngoài giả lập | M |
| | R12 Gói deploy, môi trường, quy trình thay đổi, dọn dữ liệu, bộ chẩn đoán | M |
| E. Giao diện | R13 Hoàn thiện web, lab giao diện | M |

R9–R10 làm song song với R4–R8 được. Trước R11, tin từ hệ ngoài vào bằng cách gọi thẳng command qua
suite. Từ R4, mốc nào làm luồng nào thì làm luôn màn của luồng đó. DoD chi tiết và trạng thái: [tracker](plans/tracker.md).

### Điểm chính của từng mốc

- **R1**: khai báo model bằng C# attribute; pipeline kiểm model → pre-check → handler → post-action → commit; xóa mềm; facet; trace.
- **R2**: app và extension app, POM, OData, event lưu lúc commit lên RabbitMQ, subscription, worker role, signal rule, timer, configuration key, state machine; khung web (đăng nhập, client OData, menu theo role).
- **R3**: lớp Product với từng command có test khẳng định hành vi đã quan sát (vd lệnh ghi sản phẩm vào WO không tự cộng số lượng; đóng containment không nhả hàng).
- **R4**: runner suite (bước gọi command, đọc entity, lặp theo CSV, tham chiếu kết quả bước trước, kỳ vọng lỗi, file lịch sử master data); cây thiết bị NV1; lịch ca theo khu, mẫu lệnh, timer đầu ca đóng băng lệnh cũ và mở lệnh mới; kiểm role theo transition.
- **R5**: LineDB với trigger viết tay mỗi bảng máy; bảng trung gian có trạng thái `NEW`/`SENT`/`DONE`/`NOTIFY_ERR`/`RETRY`/`FAILED`, mã lỗi ghép theo loại xử lý, chính sách gửi lại theo cặp (trạng thái, mã lỗi); hai đường chuyển tin **relay** (notify) và **poll** (signal rule); lệnh `ApplyLineResults` chọn WO Operation, tạo MTU, trừ nguyên liệu (lô trong tin hoặc MTU tạm), không trừ hai lần khi gửi lại; line simulator cho cả chuỗi.
- **R6**: cấu hình hủy theo (mã status, operation, ca, cờ chạy thử) với tách hao hụt và so đơn vị phân biệt hoa thường; NC tự sinh, tự đóng theo giờ trong tin; dấu chạy thử truyền theo genealogy; giữ hàng, hủy hàng loạt, scrap tag; containment từ `QualityTrace`; tháo rã.
- **R7**: đóng tote/pallet từ tin máy theo bảng cấu hình; post-action cập nhật số lượng và ngày đóng pallet; cất kho theo địa chỉ của part, bản ghi FIFO; bước đo `EOLV`; trailer kho; kiểm pallet (cây thành phần, số liệu, hàng lỗi); gói lẻ.
- **R8**: đơn bán từ `ERP` (Ack từng đơn), nhóm đơn, picking theo ngày đóng pallet, staging, chất xe (họ B chờ `CustomerShipSys` xác nhận), ngoại lệ theo thứ tự phát hiện, vận đơn, xe rời, ASN; nhãn ra file XML.
- **R9**: trailer, lịch xe từ `SupplierPortal`, placard, yard pass, xe không lịch, yêu cầu kéo xe, nhận skid (barcode độ dài cố định, logical dock, vị trí mặc định), chuyển skid giữa hai xe.
- **R10**: kanban và địa chỉ part; bốn tier, buffer, route; gọi hàng, nhận việc, quét lô, tự bù chặng dưới, picklist cho `WMS`; bảng gọi hàng; nhận nguyên liệu đặt thẳng; nạp lô vào buffer của khu.
- **R11**: gateway (Web API, Kafka, file; workflow gọi command); tin ra `ERP` có lịch chặn và hàng đợi; file scrap; tin đặt bù `ReorderSvc`; hệ ngoài giả lập.
- **R12**: solution CLI (gói, deploy theo bước, master data theo thứ tự); phần cài tay ngoài gói và công cụ kiểm; ba môi trường; quy trình xin thay đổi có rollback; dọn dữ liệu bằng SQL Agent; dashboard theo `CorrelationId`, script OData chỉ đọc, sổ tay SQL chỉ đọc.
- **R13**: rà đủ màn theo khu và vai, đo hiệu năng, lab Playwright.

### Khi nào v2 xong

- `make up` + deploy môi trường QA từ máy sạch ≤ 15 phút; suite master data xanh.
- Line simulator chạy 2 ngày × 4 line: số WO, MTU, dòng tiêu thụ khớp kịch bản; trạng thái các dòng tin khớp lỗi đã cài.
- Bộ suite chấp nhận của R6–R10 xanh; hai họ sản phẩm giao tới Shipped.
- Mọi lab ở §6 tái hiện bằng một lệnh và có đường chẩn đoán trong runbook.
- Test kiến trúc ép luật lớp; CI xanh, toàn suite < 10 phút; kiểm clean-room xanh.
- Luồng chính chạy trọn trên web theo từng vai.

## 6. Lab sự cố

| Lab | Tình huống | Mốc |
|---|---|---|
| L01 | Composite báo lỗi giữa chừng, các bước trước đã ghi | R1, R6 |
| L02 | `Succeeded: true` mà không có dữ liệu vì commit hỏng | R1, R12 |
| L03 | JOIN SQL thiếu `IsDeleted` đếm cả bản đã xóa | R1, R7 |
| L04 | Lệnh xong mà việc phía sau không chạy vì thiếu subscription | R2 |
| L05 | Token hết hạn giữa khối lệnh | R2 |
| L06 | Role gán cho transition nhưng không tồn tại: không ai hủy được lệnh đã đóng băng | R4 |
| L07 | Suite lỗi vẫn vào file lịch sử, lần deploy sau bị bỏ qua | R4 |
| L08 | Khung ca so theo UTC, timer tính theo giờ server | R4 |
| L09 | Lệnh thành công nhưng serial nằm trong danh sách chưa ghi | R5 |
| L10 | Tin kẹt ở LineDB sau khi đã sửa nguyên nhân | R5 |
| L11 | Payload vượt giới hạn notify | R5 |
| L12 | Tách hao hụt hỏng vì đơn vị khác hoa thường | R6 |
| L13 | Màn chỉ báo "Bad Request", lý do nằm trong thân lỗi | R6, R13 |
| L14 | Suite xanh mà thiếu cấu hình chạy thử vì khóa kiểm "đã có" thiếu cột | R6 |
| L15 | HTTP 200 mà màn trống vì thiếu view | R6 |
| L16 | Suite xanh mà gán sai class vì dòng tiêu đề CSV | R4 |
| L17 | Thiếu stored procedure sau deploy | R7 |
| L18 | Thiếu configuration key | R7 |
| L19 | Barcode lệch một ký tự; số đơn dài hơn ô của nó | R9 |
| L20 | Nạp lô bị từ chối vì lô còn ở buffer khác | R10 |
| L21 | Tin ra ERP "không đi" vì ngày bị chặn | R11 |
| L22 | Bản vá tay vào DB mất sau bước cập nhật database | R12 |
| L23 | Deploy hỏng vì tiến trình đang giữ file | R12 |
| L24 | Dữ liệu cũ biến mất do job dọn dữ liệu | R12 |

Mỗi lab có: lệnh tái hiện, triệu chứng người dùng thấy, đường chẩn đoán (thân lỗi → trace theo
`CorrelationId` → dữ liệu qua OData/SQL → code), cách sửa, và một đối chứng chạy được.

## 7. Chỗ chưa rõ: làm cả hai biến thể

Chỗ nào hành vi của hệ thật chưa xác minh thì v2 dựng cả hai, chọn bằng cấu hình, ghi rõ là giả định:
đường chuyển tin (relay hay poll); chỗ lưu trạng thái tin (cột theo môi trường hay bảng riêng); nội
dung chính sách gửi lại; múi giờ server; signal rule và adapter nào bật; đường vào của đơn nguyên liệu
đặt thẳng; part nào cần bước đo cuối.

## 8. Rủi ro

| Rủi ro | Cách giữ |
|---|---|
| Phạm vi lớn | Chỉ dựng hành vi có trong §3.1 và danh mục lớp Product; không thêm thứ hệ thật không có |
| Lộ dữ liệu dự án thật | K14, hook pre-commit, job CI clean-room |
| Mô phỏng sai hệ thật | Mỗi hành vi có nguồn quan sát (giữ riêng); chỗ chưa rõ dựng hai biến thể |
| Không còn luyện Mendix | Ngoài phạm vi repo này (ADR-053) |
| Máy không đủ RAM | Bỏ EMQX, Timescale, MinIO; đo ở R1 |
