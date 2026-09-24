---
title: "M4 — Mendix nhập môn: Operator Station v1"
milestone: M4
duration: "2 tuần theo scope; ước lượng lại sau C02 nếu connector không tương thích"
status: in_progress # C01–C05 đã commit; C06 backend qua integration; C07 runtime qua save/retry/restart và hai site; C08–C10 còn mở.
created: 2026-09-07
depends_on: [M0, M1, M2, M3]
unlocks: [M5]
---

# M4 — Mendix nhập môn: Operator Station v1

M4 dựng vòng **đăng nhập → đọc công việc → quét serial → gửi kết quả đo** bằng Mendix.
Agent triển khai màn hình và backend; chủ repo chỉ hỗ trợ thao tác Studio Pro mà MCP không thực hiện được.
Một kết quả đo được ghi nhận chưa đồng nghĩa với hoàn tất công đoạn hay release sản phẩm.

Đọc [AGENTS.md](../../AGENTS.md) §4/§5.6/§5.8, [scope.md](../scope.md)
§5.3/§5.5/§6.1/§6.2/§7.2/§7.5/§9/M4, [glossary.md](../glossary.md),
[ADR-010](../adr/ADR-010-idempotency-key-uuid-v5.md),
[ADR-022](../adr/ADR-022-publish-truc-tiep-khong-outbox-o-m1.md),
[ADR-023](../adr/ADR-023-claim-truoc-khi-chay-handler.md) và
[skill Mendix của repo](../../.claude/skills/mendix-manual/SKILL.md).

## 1. Definition of Done

Giữ đủ tám tiêu chí của scope. Các con số dưới đây là **ngưỡng/phép kiểm dự kiến**, chưa phải kết quả.
Chỉ đóng M4 khi có bằng chứng chạy thật cho D1–D8, lab và sản phẩm kèm theo.

| # | Tiêu chí | Phép kiểm và bằng chứng |
|---|---|---|
| D1 | Đăng nhập Keycloak, role map đúng sang Mendix module role | Kiểm `Operator`, `LineLeader` với tài khoản NV1/DE1; kiểm cả quyền page, microflow và API. Token thiếu/hết hạn/sai audience bị từ chối |
| D2 | Serial không tồn tại → thông báo tiếng Việt rõ ràng, không lỗi kỹ thuật | Quét serial đúng format nhưng không có; phân biệt với serial sai format và backend không phản hồi. Unit ngoài site không bị lộ qua thông báo |
| ★ D3 | Submit data collection → thấy event trên RabbitMQ management UI | Một submission hợp lệ tạo một bản ghi nghiệp vụ SQL Server và event tương ứng trong queue quan sát; đối chiếu `SiteId`, event ID, serial, giá trị nhập |
| ★ D4 | Cùng command và `idempotencyKey` trả outcome cũ, không ghi lần hai | Gửi tuần tự và đồng thời qua hai instance/connection độc lập; so outcome và `count(*)`, không chỉ đếm HTTP 200 |
| ★ D5 | Replay sau restart process vẫn đúng outcome và số bản ghi không đổi | Gửi → xác nhận commit → restart backend, giữ DB → gửi lại cùng request. Có thêm trường hợp response đầu bị mất sau commit |
| D6 | Production host từ chối `InMemoryIdempotencyStore` | Khởi động với `ASPNETCORE_ENVIRONMENT=Production` và cấu hình sai phải thất bại; đăng ký SQL đúng thì khởi động được |
| D7 | Page load **p95 < 1,5 s** theo N12 | Đo bằng browser và Mendix trace theo §5.2; lưu phân phối theo màn hình/site, dữ liệu và cấu hình đã đo |
| D8 | User NV1 không thấy bất kỳ dữ liệu DE1 nào | Kiểm grid, scan, entity theo key, `$count`, paging, filter tự sửa, command và draft Mendix; kiểm chiều DE1 → NV1 tương tự |

**Lab bắt buộc:** tắt `Nvm.App.Execution`; sau khi lưu draft thành công, submit phải hiện đúng
**"hệ thống tạm thời không phản hồi, dữ liệu đã được ghi tạm"**. Reload vẫn thấy draft;
backend trở lại thì người dùng gửi lại cùng khoá và chỉ có một bản ghi. Chi tiết §5.3.

**Sản phẩm kèm theo:** test có khả năng bắt lỗi thực; `make ci` xanh ở gate cuối;
ADR-007/014/038 ở C01, ADR-013 ở C02 sau PoC; event v1 có golden file; số đo trong
[benchmarks.md](../benchmarks.md); hướng dẫn sự cố trong [runbook.md](../runbook.md);
[oef-mapping.md](../oef-mapping.md) cập nhật theo năng lực đã triển khai và kiểm chứng.

## 2. Điểm xuất phát và các quyết định trước khi code

### 2.1 Baseline đã đọc ngày 2026-09-07

| Thành phần | Đã có | M4 cần bổ sung |
|---|---|---|
| Backend | `Nvm.Host.All`, FactoryModel, kernel command pipeline, bus, production calendar | `Nvm.App.Execution`, POM và một lát cắt ghi nhận kết quả đo |
| Idempotency | `Claim/Complete/Abandon` với store in-memory; chưa có transaction chung với business effect | SQL Server store, outcome bền vững, transaction và guard Production theo ADR-023 |
| Dữ liệu | M2/M3 sinh telemetry, chưa có `ProductionUnits` hay WIP read model | Fixture riêng cho ba tập POM; không suy ra unit từ channel khi chưa có ánh xạ |
| Mendix | App local `C:\Users\Kingc\Mendix\NvmShopFloor-main\NvmShopFloor.mpr`; Team Server repo riêng, checkout sạch tại `cc8c9db` lúc lập plan | Tiếp tục app hiện có; không tạo lại app hoặc đặt bản sao `.mpr` vào repo .NET |
| Auth | Studio Pro 11.12.3, OIDC 4.7.0; `NvmShared.NvmAccount.SiteId`, `DS_NvmAccount_Current`, `CustomATP_KeycloakRealmRoles` đã có | Kiểm lại claims/roles và luồng access token tới backend; không làm lại toàn bộ walking skeleton M0 |

M3 code đã commit tại `be05e43`. Bằng chứng M2/M3 là lịch sử, cần audit lại trước khi đánh dấu đóng;
việc viết plan M4 không tự đóng hai milestone đó. Trước triển khai, kiểm lại checkout/runtime hiện tại.

### 2.2 C01 — quyết định hiện hành

C01 hoàn tất phần tài liệu theo yêu cầu thực hiện plan ngày 2026-09-07. Scope đã đồng bộ các quyết định
dưới đây; tám DoD và K1–K13 giữ nguyên. Các ADR Accepted chốt thiết kế, không chứng nhận runtime M4 đã đạt.

| Hạng mục | Contract M4 | Quyết định |
|---|---|---|
| Nguồn dữ liệu | POM ProductionUnits/WipBoard đọc projection lifecycle từ SQL event → Rabbit → PostgreSQL; command kiểm context authoritative trong SQL transaction. Fixture cũ còn cho draft PoC chưa gửi khi serial chưa có unit thật | [ADR-043](../adr/ADR-043-authoritative-unit-context-for-manual-collection.md) |
| Phép đo / command | Điện áp pack tại `EOL`, `PackVoltage`, `V`; `RecordDataCollection` → `DataCollectionRecorded` v1, không đổi ba loại state | ADR-038 |
| Draft / quyền | Draft bền vững trong DB Mendix trước POST; retry thủ công. Operator/LineLeader cùng quyền thao tác trong site, chỉ truy cập draft chính mình; không override/release | [ADR-014](../adr/ADR-014-mendix-ui-va-draft-ben-vung.md) |
| Serial | Dùng parser 16 ký tự hiện có; không tự sửa mã hoặc tạo allocator ở M4 | [ADR-007](../adr/ADR-007-dinh-dang-serial-number.md) |

ADR-013 vẫn ở C02: chốt OData/metadata bằng PoC trên Mendix thật, không sửa nội dung một ADR đã Accepted
để bổ sung kết quả về sau. Chi tiết DI/schema của store được triển khai ở C05; contract UUIDv5 nhập tay,
identity/payload conflict và outcome đã chốt ở ADR-038, không đổi key hay event cũ.

### 2.3 Ba tình huống để đọc trước diff C01

Đây là **ví dụ contract, chưa chạy runtime**. Giá trị 401,25 V chỉ là dữ liệu minh hoạ, không phải giới
hạn chất lượng. Fixture NV1 dùng serial `NV1PP16250A00001`, operation run `OPRUN-NV1-EOL-0001`,
resource `NOVAVOLT/NV1/PACK/P1/EOL-01`; backend kiểm đúng quan hệ giữa cả ba.

| Tình huống | Kết quả backend / UI | Điều không được suy ra |
|---|---|---|
| Hợp lệ: user NV1, operation run `Running`, quality state `Pending`, nhập `PackVoltage=401.25`, `unitOfMeasure=V` | Một bản ghi kết quả + outcome commit; publish event sau commit; UI báo đã ghi nhận | Pack đã đạt chất lượng hoặc đã hoàn tất `EOL` |
| Bị chặn: cùng context nhưng pack `Held` | `accepted=false`, lý do pack đang bị giữ; không có bản ghi kết quả/event. Outcome từ chối được lưu; UI cho quay lại danh sách, không có override | LineLeader được release hoặc nhập thay để bỏ qua hold |
| Timeout: draft đã `Pending`, không nhận được response POST | Giữ nguyên key/payload và báo chưa xác nhận; nếu Execution không phản hồi, hiện câu lab. Gửi lại để lấy outcome cũ hoặc thực hiện nếu lần đầu chưa commit | Chắc chắn DB chưa ghi, hoặc có thể tạo key mới mà không gây trùng |

Serial parser hiện có đã chạy **22/22 test xanh** ở C01; bằng chứng/lệnh trong ADR-007.
Các case trên phải thành test và thao tác thực tế ở C05–C09, không đánh dấu D3–D5 chỉ từ bảng này.

## 3. Thiết kế đủ cho M4

### 3.1 Biên triển khai và dữ liệu

```text
Browser → Mendix NvmShopFloor
           ├─ DB riêng: account, draft của người dùng
           ├─ GET POM + access token → Nvm.App.Execution → PostgreSQL read model
           └─ POST command + access token → SQL Server transaction → publish RabbitMQ
```

`Nvm.App.Execution` và `Nvm.Host.All` dùng chung module registration, route và policy.
Dev có thể chạy Host.All; lab chạy Execution thật. Cấu hình port/base URL riêng, kiểm process nhận
request trước khi đo. Execution chỉ ở `it-net`; Mendix không nhận connection string DB của .NET (K10/K11).

| Nơi sở hữu | Dữ liệu / trách nhiệm tối thiểu |
|---|---|
| `Nvm.PublicObjectModel` | Ba entity set read-only, DTO/EDM riêng, query được giới hạn và scope theo token |
| `ProductionExecution` | Context hợp lệ để nhập kết quả; command, handler và bản ghi `DataCollection` append-only, có actor và thời điểm |
| SQL Server | Context fixture, bảng claim/outcome và bảng kết quả đo; effect + claim + outcome cùng connection/transaction theo ADR-001/023 |
| PostgreSQL | Read model của `ProductionUnits`, `WipBoard`, `Equipment`, theo ADR-002; không làm write store cho command |
| Mendix | `NvmShared` chứa account, kết nối có auth và response mapper; module `NvmShopFloor` sở hữu draft và màn hình nghiệp vụ |

Fixture mặc định **1.000 unit/site**, có line/resource, work order, operation run, step, ba loại state
tách biệt và lý do chặn. Số này phục vụ phép đo M4, không tuyên bố quy mô cả nhà máy.
Serial đi qua `SerialNumber` hiện có; resource khớp catalog revision 3, có `PACK/P1/EOL-01` tại hai site.
Dùng công đoạn phù hợp từng site,
không seed formation cho DE1. Seed chạy tường minh, có thể lặp, không chạy trong startup Production,
không sửa/xoá kết quả người dùng hoặc dữ liệu telemetry M3.

POM ProductionUnits/WipBoard đã chuyển sang projection lifecycle thật; gửi kết quả đo **không làm
unit chuyển bước**. Metadata giữ entity/key, mở rộng độ dài sáu thuộc tính. Fixture và bản ghi đã nhận
được giữ; fixture không trộn vào danh sách unit thật. Guard SQL của command luôn ưu tiên unit thật,
không dùng projection trễ để quyết định quyền ghi. Quality release/location movement còn mở.

### 3.2 POM và phép thử Mendix sớm

| Entity set | Dữ liệu cần cho màn hình |
|---|---|
| `ProductionUnits` | Key, `SiteId`, serial, line/resource, work order/operation run, step, execution state, quality state, location, lý do chặn |
| `WipBoard` | Key, `SiteId`, line, step, quality state, số unit |
| `Equipment` | Key, `SiteId`, equipment path, tên hiển thị, line/resource |

Dùng ASP.NET Core OData với provider PostgreSQL dịch query sang SQL; pin package tương thích .NET 10
sau PoC. Không tự viết parser OData hay load toàn bộ bảng rồi lọc trong RAM.
Key là `Edm.String` có `MaxLength` hữu hạn; serial là field 16 ký tự, không copy serial thiếu ký tự trong
ví dụ scope. Ưu tiên DTO phẳng cho M4. Connector có giới hạn về key/complex collection/navigation và
Mendix không bảo đảm mọi third-party OData service đều dùng được: **import metadata rồi đọc thật ngay C02**.
[Yêu cầu OData của Mendix](https://docs.mendix.com/refguide/consumed-odata-service-requirements/).

- Ép predicate `SiteId` từ principal đã xác thực **trước** mọi filter, count, lookup và paging.
  Key thuộc site khác trả kết quả không tiết lộ sự tồn tại; filter do client gửi không thay predicate này.
- Page size mặc định 50, `$top` tối đa 1.000; thứ tự ổn định có key phân xử. Trả `@odata.nextLink`
  khi còn trang, bỏ ở trang cuối. Scope/ADR-038 chốt ngữ nghĩa; C02 chứng minh connector đọc được.
- Có `ETag` cho entity đơn; kiểm scope cả với request conditional. Chỉ hỗ trợ query option/navigation
  đã implement và kiểm giới hạn; không quảng cáo `$expand=Genealogy/Measurements` của milestone sau.
- Endpoint POM chỉ đọc. `$metadata` không chứa secret; import snapshot metadata khi import trực tiếp
  không mang được token. Không mở anonymous data endpoint để làm connector chạy.

PoC dùng một grid Equipment đã auth, page tiếp theo, filter và account site khác trên **Studio Pro 11.12.3**.
Headers microflow cung cấp access token phía server cho từng request; error handler xử lý cả response rỗng.
Tài liệu áp dụng 11.12 trở xuống mô tả các điểm mở rộng này tại
[Consumed OData Service](https://docs.mendix.com/refguide/consumed-odata-service/).
Nếu PoC thất bại, lưu lỗi/metadata tối thiểu và chốt cách tích hợp trước khi dựng các màn hình còn lại;
không âm thầm thay OData bằng API khác hoặc tạo framework connector chung.

### 3.3 Identity, quyền và dữ liệu theo site

Giữ cấu hình OIDC M0; xác minh lại `site_id`, `mendix_roles` và audience dành cho Execution.
Backend xác thực signature, issuer, audience, thời hạn và quyền của **user hiện tại**;
token thiếu site bị từ chối. `siteId` trong body phải khớp principal; account/draft do Mendix lưu
không thay thế xác thực ở API. Không dùng một service account toàn quyền cho mọi người dùng.

`Operator` và `LineLeader` có quyền vào các màn hình M4 và gửi kết quả trong site của mình;
LineLeader chưa có chức năng override/release trong M4. Áp dụng module role, microflow access,
entity access và XPath theo site/owner cho dữ liệu persistent Mendix. Dữ liệu external entity vẫn
được chặn ở POM; ẩn nút, filter grid hoặc XPath của draft không chứng minh K3 cho backend.

Giữ token trong cơ chế OIDC phía server, không chép vào entity nghiệp vụ, URL hay log. Secret dùng
nguồn cấu hình hiện có; tài liệu chỉ ghi tên khoá. Chốt entry page có login rõ ràng và kiểm callback;
không lấy HTTP 200 của trang anonymous làm bằng chứng D1.

### 3.4 Command và idempotency bền vững

Contract: `POST /api/v1/commands/production/record-data-collection`, envelope theo scope §7.5.
Payload gồm submission ID ổn định, serial, operation run/step, equipment path, signal, giá trị và đơn vị.
Phép đo: **điện áp pack tại step `EOL` (End-of-Line)**, `signalCode=PackVoltage`,
giá trị số thập phân, `unitOfMeasure=V`, dùng được ở khu PACK của cả NV1/DE1. Kiểm kiểu/đơn vị và context;
chưa tự kết luận đạt/không đạt khi chưa định nghĩa giới hạn chất lượng. Một form cố định là đủ cho lát cắt này.
Actor lấy từ principal; `occurredAt` giữ nguyên khi retry, backend ghi `recordedAt` bằng `TimeProvider`.
Không giả tạo device/gateway timestamp để tái sử dụng event `MeasurementRecorded` của luồng thiết bị.

Backend kiểm context SQL trong site: unit là pack, operation run thuộc pack/step `EOL`, resource đúng
trạm được giao, operation run `Running` và pack không `Held`/`Scrapped`. Lý do chặn trả theo
`{accepted, reasonCode, reasonText, blockingRules, allowedNextActions, correlationId}`; UI chỉ hiển thị
hành động đã có màn hình xử lý. Không đưa `RequestOverride` vào response khi chưa có chức năng đó.
Thiếu token/sai quyền là 401/403; input hỏng và payload xung đột có response rõ ràng; không lộ stack trace.

```text
Xác thực + kiểm hình dạng request
  → BEGIN SQL transaction
  → claim khoá bằng unique constraint
  → nếu đã xong: kiểm identity/payload, đọc outcome cũ
  → nếu mới: kiểm điều kiện nghiệp vụ, ghi kết quả, lưu outcome
  → COMMIT
  → chỉ lần xử lý mới publish event; trả outcome
```

- Giữ API `Claim/Complete/Abandon` làm điểm xuất phát. Store và handler dùng **chính transaction đó**;
  không để DI tạo hai connection/transaction độc lập. SQL implementation ở infrastructure, không kéo
  SqlClient/EF vào domain/kernel. Không đăng ký SQL store scoped sau một behavior giữ singleton state.
- Khoá theo ADR-010: UUIDv5 từ namespace hiện có và tuple `(site, commandType, submissionId)` mã hoá
  như helper kernel. Theo [ADR-041](../adr/ADR-041-server-derived-manual-submission-key.md), Mendix tạo
  submission ID **một lần**, giữ trong draft và có thể bỏ `idempotencyKey` ở request; backend luôn suy
  cùng key khi retry. Nếu client gửi key, backend kiểm vector chéo với .NET và từ chối key lệch.
- Lưu `SiteId`, actor, command type, fingerprint payload và outcome. Cùng key nhưng payload/actor
  khác bị từ chối, không trả dữ liệu người khác. Mọi query claim/outcome kèm site đã xác thực;
  backend kiểm key khớp natural key của request. Không đưa field đổi khi retry vào natural key.
- Validation hình dạng nằm trước claim; điều kiện nghiệp vụ có thể đổi nằm trên nhánh xử lý mới.
  Replay đã được chấp nhận phải trả outcome cũ kể cả khi state hiện tại đã đổi, sau khi auth vẫn hợp lệ.
- Rejection nghiệp vụ lưu outcome, không ghi kết quả đo/event; replay vẫn trả rejection đó. Sau khi
  điều kiện thay đổi, một ý định mới cần submission mới tường minh. Lỗi hạ tầng rollback cho phép retry
  cùng key; không nhầm với rejection đã có kết luận (ADR-038).
- Transaction rollback giải phóng claim chưa commit. Crash trước commit cho phép retry; crash sau
  commit trả outcome cũ. Timeout/không biết commit đã thành công không được xoá một claim đã commit.
  Tranh chấp khoá chờ có giới hạn; không thêm lease, Redis hoặc distributed lock.
- Production guard áp dụng cả Execution lẫn Host.All nếu chạy Production. Các endpoint bus/dev của
  FactoryModel vẫn chỉ ở Development; không dùng durable claim che một effect activation còn in-memory.

Event `DataCollectionRecorded` có `[EventVersion(1)]`, golden file và CloudEvents theo contract hiện có;
`EventId = ce_id = idempotencyKey` cho một event từ command. Handler ghi `es.Events` và `es.Outbox`
cùng transaction với kết quả đo và outcome. Worker publish sau commit, retry từ SQL qua lỗi broker/process;
replay command không tạo thêm event/outbox. Stream `data-collection:<SubmissionId>` chứa đúng một fact,
không giả định kết quả đo làm đổi trạng thái ProductionUnit. Transport là at-least-once: consumer vẫn phải dedup EventId.
Bản tích hợp qua 48/48 HTTP + SQL outbox tests (109,354s) sau forced rebuild; review độc lập không còn finding trong hai bản sửa lease và envelope. Chưa nghiệm thu toàn bộ N3.

### 3.5 Draft và bốn màn hình

`NvmShopFloor.DataCollectionDraft` nằm trong **DB của Mendix**, giữ dữ liệu chờ backend xác nhận. Có owner, `SiteId`,
submission ID, payload, thời điểm, idempotency key nếu đã biết và trạng thái `Editing`, `Pending`, `Accepted`, `Rejected`
theo ADR-014; chỉ draft `Editing` được sửa, `Pending` retry giữ nguyên request.
Không phải bản sao write model hay một business event đã được backend chấp nhận.

Lưu draft bằng một request/microflow **đã hoàn tất transaction**, rồi mới gọi POST ở request tiếp theo.
Một `Commit object` nằm trong microflow dài còn có thể rollback không đủ chứng minh đã ghi tạm.
[Ngữ nghĩa Commit Object của Mendix](https://docs.mendix.com/refguide/committing-objects/).
Microflow phía server đóng băng khoá/payload và lưu trạng thái chờ trước lần POST đầu;
timeout giữ trạng thái chờ và gửi lại đúng request.
Muốn ghi kết quả khác thì tạo submission mới tường minh, không âm thầm cấp key mới sau timeout.

| Màn hình | Hành vi đủ dùng |
|---|---|
| Dispatch list | Chọn line/resource, thấy việc trong site và mở đúng context; phân trang phía server |
| Scan station | Nhận serial từ scanner như bàn phím; hiện state tách biệt, lý do chặn và bước tiếp có thể làm; unknown serial và mất kết nối có thông báo khác nhau |
| Data collection | Form cố định cho phép đo demo, lưu draft rồi submit; chống double-click ở UI, idempotency bảo vệ ở server; xem lại draft và retry thủ công |
| WIP board | Nhóm theo line/step/quality state; auto-refresh mặc định 5 giây, không chồng request hay xoá input đang nhập; hiển thị thời điểm lần đọc thành công và trạng thái mất kết nối |

Lưu draft thất bại thì báo không lưu được, **không** hiện câu "đã được ghi tạm". Timeout sau POST có thể
là backend đã commit: UI báo đang chờ xác nhận và giữ nguyên draft, không kết luận thao tác thất bại.
M4 không làm offline khi Mendix server mất kết nối, background sync, service worker hoặc tự xử lý conflict.
Connector có timeout cấu hình tường minh, khởi đầu 10 giây rồi ghi thời gian thật trong lab;
đây là giới hạn chờ lỗi, không thay ngưỡng page load 1,5 giây.

## 4. Kế hoạch theo lát cắt triển khai

Mỗi dòng là một lát cắt có thể review và kiểm chứng. Agent tiếp tục qua các dòng trong phạm vi dự án,
không chờ owner commit hoặc làm bài học. Agent được tự commit/push phần đã kiểm chứng.
Thay đổi .NET và Mendix ở hai repo: khi một bước cần cả hai, ghi **cặp SHA** đã kiểm trong bằng chứng.
Agent sửa model Mendix qua công cụ được hỗ trợ; chỉ nhờ owner thao tác UI mà agent không thực hiện được.

| Commit | Mục tiêu / nơi sửa chính | Kiểm chứng trước khi giao |
|---|---|---|
| C01 · `docs(m4): define operator data collection boundaries` · **tài liệu xong** | Scope, ADR-007/014/038, glossary và catalog đã chốt contract §2.2 | Ba ví dụ review ở §2.3; parser 22/22. Chưa có runtime M4; phần triển khai bắt đầu C02 |
| C02 · `feat(pom): prove authenticated Mendix OData reads` · **xong** | Đã có Execution/POM, Host.All registration, PG migration/fixture 6 Equipment, JWT policy, package pin và metadata snapshot; hướng dẫn/bằng chứng ở [deploy/pom](../../deploy/pom/README.md). Owner đã import `NvmShared.POM_v1`, gán URL constant và hai microflow headers/error handling; hai microflow 0 lỗi model, dump xác nhận URL/OData4 và binding. External entity Equipment có quyền ReadOnly cho NvmShared.User. Grid `NvmShopFloor.Equipment_PoC` có 7 cột, page size 2; app role Operator/LineLeader đã gán module role và role-based home page | 21 test POM + 23 architecture xanh; `make ci` xanh. Execution/Host.All đã đọc bằng token Keycloak thật ở cả hai site: trang 2+1 row, cross-site filter 0, key 404, anonymous 401; metadata runtime khớp snapshot. Port cũ hoạt động và audience mapper đã áp dụng. Mendix runtime: grid hiện đúng dữ liệu site, phân trang 2+1, cross-site isolation đúng, SSO login/role-based home page hoạt động. [ADR-013](../adr/ADR-013-odata-cho-public-object-model.md) chốt connector pattern |
| C03 · `feat(pom): expose operator read models` · **xong** | Backend có ba entity set §3.2, fixture 1.000 unit/site, site filters/ETag/paging. Owner đã import `Pom.metadata.xml` vào POM_v1: entity `ProductionUnits` (16 attr) và `WipBoard` (7 attr), ReadOnly cho `NvmShared.User`, default None, 0 errors model. URL constant, headers/error microflow giữ nguyên | .NET `da9b08c`, Mendix `d1bb804`; owner xác nhận đã hoàn thành và commit cả hai repo. 25 test mới/thay đổi pass qua các lượt targeted; Docker đã seed, readiness 200. Import schema xong; runtime hai entity mới được kiểm khi dựng màn hình C04/C08 |
| C04 · `feat(shopfloor): add dispatch and scan pages` · **xong** | Dispatch/Scan, quyền truy cập, VAL/SUB/ACT và nhánh lỗi đã dựng; agent hoàn thiện qua MCP theo yêu cầu owner. Phạm vi và checklist ở §4.1 | .NET `43e4b05`, Mendix `1e445d9`. Toàn app 0 errors; browser Operator NV1/DE1 và LineLeader đạt; filter/paging xác nhận tại POM, cách ly hai chiều, lỗi kết nối xoá kết quả cũ và retry được. Bằng chứng ở benchmarks |
| C05 · `feat(kernel): persist command outcomes in SQL transactions` · **xong** | SQL store giữ claim/effect/outcome trong cùng scoped session; migration riêng, context từ 2.000 unit của fixture C03, runtime có quyền tối thiểu. Hai host có guard Production. Command RAM cũ chỉ dùng Development | Commit `8639e75`. `make ci` exit 0: 781/781 test, buffer 200/200 vòng; net-check 9/9. Trong đó có 36 test C05: hai process thật replay một effect; kill trước commit rollback cả claim/effect. Oracle chạy source C04 phát hiện 2 effect thay vì 1. Execution mới healthy/readiness 200. Hướng dẫn ở `deploy/commands/README.md`; số đo và giới hạn kiểm chứng ở benchmarks. Chưa mở command nghiệp vụ mới ra HTTP |
| C06 · `feat(execution): record operator data collection` | Agent tạo lát cắt `src/FunctionalBlocks/ProductionExecution/`, command/HTTP mapper, event trong Contracts, golden file và cập nhật event-catalog | D3/D4/D5 qua HTTP thật, actor/site/đơn vị/context hợp lệ, blocked case không ghi; quan sát queue trước publish; publish lỗi được báo đúng với trạng thái DB |
| C07 · `feat(shopfloor): persist submissions before sending commands` · **runtime happy path, retry và mapper đã kiểm** | Persistent draft có owner/read-only access, form lưu riêng request, `SubmissionId` sinh một lần, backend suy UUIDv5 theo ADR-041. `SUB_DataCollection_Send` POST request đóng băng; nút gửi/thử lại giữ draft Pending nếu không có response; trang danh sách mở lại draft sau reload. `mx check` 11.12.3 sau Studio Pro Ctrl+S/F4: 0 lỗi | Runtime 2026-09-23: login NV1, lưu/gửi, reload mở lại; backend down giữ Pending, start/reload/retry cùng submission ghi đúng 1 row SQL. Còn bản nháp Pending qua restart, auth/site âm và mất ACK; đã nối IM_CommandResponse để đọc Accepted/ReasonCode/ReasonText/CorrelationId, lỗi parse giữ Pending; MCP/mx check 0 lỗi. Runtime mới 2026-09-23: submission f890e540-0823-4175-910e-d3700832eb3e đi vào nhánh không xác nhận khi thử sai operation run; Console xác nhận NoSuchElementException tại MappingCache.storeValueMappingElement với Path rỗng. Codex 2026-09-23 đối chiếu bytecode runtime 11.12.3: forceSingleOccurrence=true gọi dropLeft trên mọi path, bỏ root (Object). Đã sửa cấu hình do agent tạo thành false qua MCP, đọc lại xác nhận và check 0 lỗi. Runtime sau Ctrl+S/F5: gửi lại cùng submission đã hiện đúng lý do và trạng thái Bị từ chối; submission mới 3ac6b0a1-7f98-42ea-86a3-0333263fcb74, 405.375V, được Đã ghi nhận. Danh sách draft xác nhận cả hai trạng thái. Owner đã Stop/F5; agent đăng nhập lại qua OIDC và kiểm danh sách: cả 4 draft giữ nguyên ID, số đo và trạng thái (3 accepted, 1 rejected). SQL draft mới đúng 1 row; draft rejected 0 row. Ngày 2026-09-24: draft DE1 d7585761-7f2a-4645-9a0a-cfd90c9d0ccd giữ nguyên Pending qua Stop/F5, gửi sau restart thành công đúng 1 SQL row; DE1 không thấy draft NV1 và bị chặn lưu serial NV1. SQL outcome có OPERATION_RUN_MISMATCH với key 74031ee9-fee8-5a9f-bbe8-71ba0ead1e05 (đã tính UUIDv5 từ đúng submission), 0 DataCollection row |
| C08 · `feat(shopfloor): add the WIP board and recovery actions` | Agent dựng WIP board, auto-refresh, thông báo lỗi và đường mở/gửi lại draft | Đếm đúng theo fixture, không chồng request; form không mất input; operator đi hết happy path mà không gặp exception kỹ thuật |
| C09 · `test(m4): verify isolation restart recovery and page latency` | Agent chạy phép kiểm UI/API và lab khi công cụ hỗ trợ; nhờ owner phần UI không thể tự thao tác | Toàn bộ §5, số đo thật ghi benchmarks/runbook; đọc diff và targeted tests sạch rồi mới `make ci` |
| C10 · `docs(m4): record verification outcomes` | Hoàn thiện plan, ADR index, event-catalog, runbook, oef-mapping; audit độc lập theo AGENTS | §6 sạch, §7 có bằng chứng kỹ thuật. Không nâng `status` khi còn DoD chưa đạt |

**TDD từ M4:** với phép kiểm yêu cầu RED, lưu parent SHA, diff chỉ chứa test, lệnh và assertion đỏ,
rồi GREEN của implementation theo [ADR-034](../adr/ADR-034-dieu-kien-nghiem-thu-m3-sau-audit.md).
Nếu parent chưa có seam cần gọi, chuẩn bị scaffold build được ở commit trước. Thiếu type/table làm
build/query lỗi không thay cho assertion bắt hành vi sai; không cần commit đỏ lên `main`.

Không viết sẵn hàng trăm bước click dựa vào trí nhớ Studio Pro. Mỗi lượt Mendix đọc trạng thái hiện tại,
giao một khối thao tác đúng phiên bản, kiểm rồi mới giao khối tiếp theo. Hướng dẫn thao tác, giải thích
nghiệp vụ và giải thích kỹ thuật tách riêng theo AGENTS.

### 4.1 C04 — Dispatch list và Scan station

**Mục tiêu:** tìm việc theo line/resource, mở hoặc quét đúng serial, hiển thị context và lý do bị chặn.
C04 hoàn tất khi hai màn hình chạy bằng tài khoản thật và các nhánh tra cứu được kiểm. Tạo page rồi
thấy 0 errors chưa đủ. Thiết kế dưới đây đã dựng và kiểm chức năng; bằng chứng nằm ở cuối mục và benchmarks.

Điểm bắt đầu: .NET `da9b08c`, Mendix `d1bb804`. Dùng lại `NvmShared.POM_v1`, external entity và role
đã có; document mới đặt trong folder `NvmShopFloor/ProductionUnits`. Không di chuyển Equipment_PoC.

#### Nghiệp vụ

Dispatch list M4 hiển thị unit có work order/operation run trong fixture, lọc theo nơi đang xử lý;
chưa có bộ lập lịch hay giao lại việc. Serial giữ định danh lúc sinh: cell tại FORM/F1 vẫn có thể mang
line L1 trong serial. Không dùng phần line khắc trên serial làm vị trí hiện tại.

Scan hiển thị execution state, quality state và location state riêng. Tìm thấy unit không có nghĩa
là được phép nhập kết quả: hiển thị lý do Held/Scrapped hoặc operation chưa chạy/đã hoàn tất.
C04 có thao tác quét lại và về danh sách; form nhập kết quả được nối vào ở C07.

#### Thành phần cần dựng

| Thành phần trong NvmShopFloor | Cấu hình / trách nhiệm |
|---|---|
| `Dispatch_List` | Page không parameter; Data Grid 2, Database source `NvmShared.ProductionUnits`. Page size 20, Paging buttons; sort Line, Resource, `_Id` tăng dần. Filter cột Line/Resource dùng Text filter, phép Equal; query và paging qua OData |
| `ScanContext` | Non-persistable; SerialInput String(64), Message String(512); reference `ScanContext_ProductionUnits` tới `NvmShared.ProductionUnits`, owner là ScanContext. Giữ input/thông báo/kết quả hiện tại, không chép 16 thuộc tính vào entity khác |
| `Scan_Station` | Page nhận ScanContext; ô SerialInput, nút Tra cứu, thông báo và data view qua reference kết quả. Enter và nút gọi cùng action; nút Về danh sách mở Dispatch_List |
| `SUB_ScanContext_New` | Nhận String SerialInput, tạo ScanContext trong bộ nhớ và trả object; dùng chung cho mở scan trống và mở từ Dispatch |
| `VAL_Serial_IsValid` | Nhận String, trả Boolean; kiểm đủ ngữ nghĩa SerialNumber.TryParse bên dưới |
| `SUB_ScanContext_Lookup` | Xoá kết quả/thông báo cũ, validate, retrieve external entity theo serial với Range First; gán kết quả hoặc thông báo, refresh ScanContext; không commit DB |
| `ACT_ScanStation_Open` | Gọi SUB_New với chuỗi rỗng rồi mở Scan_Station |
| `ACT_Dispatch_OpenUnit` | Nhận ProductionUnits từ dòng chọn; gọi SUB_New với SerialNumber, SUB_Lookup rồi mở Scan_Station. Đọc lại POM thay vì tin context cũ trên grid |
| `ACT_ScanStation_Search` | Action dùng chung cho nút và phím Enter, gọi SUB_Lookup với ScanContext |

Các cột Dispatch: SerialNumber, WorkOrderId, Line, Resource, StepCode, ExecutionState, QualityState,
LocationState. Scan hiển thị thêm OperationRunId, EquipmentPath và BlockingReasonCode/Text.
Không sinh New/Edit/Delete cho external entity; không dùng microflow kéo 1.000 object rồi lọc trong RAM.

Page và ba ACT cấp quyền `NvmShopFloor.User`, đã map vào app role Operator/LineLeader. ScanContext
cho User đọc thuộc tính/reference, ghi SerialInput; kết quả do microflow server cập nhật. SUB/VAL là
microflow nội bộ, không mở gọi trực tiếp từ client. External entity giữ ReadOnly của NvmShared.User.
Backend vẫn ép site từ token; ScanContext/filter không trở thành nguồn authorization.

#### Contract tra cứu và lỗi

- Giữ nguyên input, không trim/chuyển chữ hoa. String(64) nhận được mã quá dài rồi báo sai, tránh
  cắt một mã dài thành serial hợp lệ một cách im lặng.
- VAL kiểm đủ 16 ký tự ASCII hoa/số: site 3 ký tự; kind C/M/P; line `[A-Z][0-9]`; year 1 digit;
  day 001–366; shift A/B/C; sequence 00001–99999. Không giới hạn site vào NV1/DE1 trong validator,
  không suy ra ngày lịch từ year digit. Chỉ parse khoảng số sau khi khung ký tự hợp lệ.
- Retrieve `NvmShared.ProductionUnits`, XPath `[SerialNumber = $ScanContext/SerialInput]`, Range First.
  Không ghép URL từ input hoặc retrieve toàn bộ list.
- Sai format: **Serial không đúng định dạng 16 ký tự. Kiểm tra lại mã quét.** Không gọi POM.
- Retrieve thành công, object rỗng: **Không tìm thấy serial trong site hiện tại.** Serial ngoài site
  nhận cùng thông báo, không tiết lộ sự tồn tại ở nơi khác.
- Retrieve ném lỗi: nhánh custom error riêng, kết quả rỗng và thông báo **Không thể tra cứu lúc này.
  Kiểm tra kết nối hoặc đăng nhập lại rồi thử lại.** Không hiện stack trace hoặc câu đã lưu draft.
  Custom without rollback giữ việc xoá kết quả cũ; refresh context trên mọi nhánh. Call từ UI có
  blocking progress, tránh hai lượt tra cứu chồng nhau.
- Timeout POM 10 giây theo §3.5. Scanner dùng **On enter key press**, không phải On enter (focus).
  Không gán tra cứu đồng thời vào On change, tránh gọi thêm khi rời ô.

Nguồn cơ chế: [External Entities](https://docs.mendix.com/refguide/external-entities/),
[Data Grid 2](https://docs.mendix.com/appstore/modules/data-grid-2/),
[Text Filter](https://docs.mendix.com/appstore/modules/datagrid-text-filter/),
[Text Box](https://docs.mendix.com/refguide/text-box/),
[Consumed OData Service](https://docs.mendix.com/refguide/consumed-odata-service/).
Tên/property cuối cùng được đối chiếu trên Studio Pro 11.12.3 khi dựng. Association local → external
do local entity sở hữu, không thêm navigation vào POM. Chưa ghi nhận UI đã kiểm chỉ từ tài liệu web.

#### Checklist thực hiện và nghiệm thu

- [x] C04-1: Dispatch_List có datasource/cột/filter/paging và page access; runtime NV1 đã kiểm ngày 2026-09-12.
- [x] C04-2: ScanContext có reference kết quả và entity access; SerialInput ReadWrite xác nhận qua MCP sau F5 ngày 2026-09-12.
- [x] C04-3: VAL/SUB/ACT có nhánh không tìm thấy và nhánh error riêng; MCP 0 errors và runtime đạt ngày 2026-09-12.
- [x] C04-4: Scan_Station hiển thị context/state/lý do; nối nút/Enter và mở từ dòng Dispatch; runtime NV1 đã kiểm.
- [x] C04-5: thêm điều hướng Dispatch/Scan; home page Operator/LineLeader chuyển sang Dispatch_List,
  giữ đường quản trị hiện có; Save All, Check now và chạy app.
- [x] C04-6: kiểm bảng dưới ở runtime, ghi bằng chứng vào benchmarks.
- [x] Owner yêu cầu commit C04 ở cả hai repo sau khi nhận bằng chứng kiểm thử.
  Page p95 và lab draft vẫn ở C09; không hoãn kiểm chức năng C04 tới C09.

| Case | Kỳ vọng cần nhìn thấy / đo được |
|---|---|
| Operator và LineLeader mở hai page | Mở được trong site mình; không thay bằng thử tài khoản admin |
| Dispatch NV1, Line=P1, Resource=EOL-01 | Chỉ việc EOL/P1 NV1; fixture có 150 unit. Trang kế không lặp dòng; server nhận filter và giới hạn trang |
| Mở dòng Dispatch | Scan hiện đúng serial, work order, operation run và resource đã chọn |
| NV1 `NV1PP16250A00001`, DE1 `DE1PP16250A00001` | Tìm thấy ở đúng site; Running/Pending/AtStation; ba state riêng |
| NV1 `NV1PP16250A00007` | Held kèm lý do giữ; không có release/override |
| NV1 `NV1PP16250A99999` | Đúng format, chưa seed → không tìm thấy |
| NV1 quét serial DE1 và ngược lại | Không tìm thấy; không lộ dữ liệu ngoài site |
| Chữ thường; 15/17 ký tự; kind X; day 000/367; sequence 00000 | Sai format, không gửi lookup. Day 366 hợp lệ về format dù chưa có fixture |
| Quét thành công rồi gây lỗi kết nối POM và tra lại | Lỗi tra cứu; không giữ unit cũ, không báo không tồn tại/đã ghi tạm. Kết nối trở lại thì retry được |
| Bỏ filter UI, gọi POM ngoài site | Backend vẫn ép site; dùng lại test C03 cho contract API, kiểm request UI mới và thao tác NV1/DE1 ở C04 |

**Trạng thái:** Dispatch_List đã dựng, có menu Dispatch và owner đã cấu hình role-based home pages,
báo 0 errors. Browser automation ngày 2026-09-12 kiểm phiên NV1: 1.000 dòng ban đầu; lọc P1/EOL-01
còn 150; trang đầu/kế/cuối có 20/20/10 dòng đúng serial, không trùng giữa ba trang đã đọc; filter
Line/Resource khớp chính xác. Số đo và giới hạn kiểm chứng ở `docs/benchmarks.md` mục M4/C04.
ScanContext và Scan_Station đã dựng; sáu VAL/SUB/ACT cùng hai page qua kiểm tra MCP không lỗi.
Lookup có nhánh lỗi riêng; nút Tra cứu/Enter dùng ACT_Search; Dispatch có nút Mở trên từng dòng;
menu Quét serial gọi ACT_Open. Sau owner F5 ngày 2026-09-12, mx check 11.12.3 kiểm bản trên đĩa
trả exit 0, toàn app 0 errors; MCP xác nhận SerialInput ReadWrite, Message/reference ReadOnly.
POM timeout 10 giây đã xác nhận bằng mx dump-mpr chỉ lọc ConsumedODataService.
Runtime NV1 đã kiểm mở từ Dispatch, Enter/nút Tra cứu, unit hợp lệ/Held, 7 ca sai format,
không tồn tại, serial DE1 và day 366. Dừng riêng Execution: hiện lỗi kết nối sau 132 ms,
xoá kết quả cũ, giữ input; bật lại healthy và retry thành công. Bằng chứng trong benchmarks.
Runtime op.de1 cũng đã kiểm mở từ Dispatch, tra cứu unit DE1 và Enter với serial NV1:
không tìm thấy, kết quả cũ rỗng. Phiên ll.nv1 xác nhận role LineLeader, mở Dispatch/Scan,
hiện lý do Held và không tìm thấy serial DE1; không có release/override.
Quan sát request tại Execution xác nhận filter Line/Resource, $top=20, $skip=20 và
lookup SerialNumber với $top=1. Ca chữ thường không phát request POM trong cửa sổ quan sát;
ca hợp lệ kế tiếp có request làm đối chứng. Chi tiết và giới hạn nằm trong benchmarks.
C02/C03 dùng lại bằng chứng owner đã nghiệm thu, không chạy lại bộ test cũ.

## 5. Kiểm chứng và lab

### 5.1 Các kiểm tra có sức bác bỏ thiết kế

| Nhóm | Phép thử phải phân biệt được |
|---|---|
| Transaction | Ném lỗi giữa business INSERT và Complete → không còn effect hoặc claim đã xong; retry thành công chỉ thêm một record |
| Restart | Commit rồi chặn response/kill process → gửi lại qua process mới vẫn đúng outcome, cùng ID/thời điểm, một record |
| Concurrency | Hai instance cùng SQL Server gửi cùng request đồng thời → một effect; rollback của caller trước không ghim claim vĩnh viễn |
| Identity | Đổi `siteId`, actor, payload, query filter hoặc key để vượt quyền → không lấy outcome/dữ liệu site khác, không ghi nhầm site |
| Context | Unit/step/equipment không khớp hoặc đang bị chặn → reason có nghĩa, không ghi record; UI không tự release hay complete |
| UI persistence | Save draft thành công rồi dừng backend/reload/restart Mendix giữ DB → draft còn; save lỗi không báo đã lưu |
| POM | Hơn một page, sort trùng giá trị, lookup theo key, count và conditional request đều giữ scope; nextLink không lặp vô hạn |

Dùng Testcontainers/DB test riêng cho lỗi transaction và restart. Test fixture không xoá dữ liệu demo
của owner. Queue quan sát D3 phải bind trước khi submit; management UI xem payload theo chế độ requeue
hoặc dùng queue riêng, không lấy mất message của consumer nghiệp vụ. Không dùng publish count để suy ra
dedup của business record; RabbitMQ vẫn là at-least-once.

### 5.2 Đo N12

- Cùng fixture 1.000 unit/site, grid page 50; ghi máy/browser/Mendix version, commit của hai repo,
  timestamp image backend, cấu hình refresh và trạng thái cache. Chưa chạy thì ghi **chưa đo**.
- Mỗi màn hình trong bốn màn hình, mỗi site: **30 lượt đo** với browser visible, ưu tiên script lặp.
  Đo từ điều hướng/mở màn hình tới dữ liệu render xong và điều khiển dùng được; lấy Mendix trace
  để tách backend/network/render. `curl` trả HTML không phải page load.
- Đo hai trường hợp riêng: mở trang sau login trong browser session mới, và điều hướng lại trong session
  đã dùng. Tính p95 nearest-rank của từng tập, cùng ngưỡng 1,5 giây; không gộp trang nhanh che trang chậm.
  Không xoá cache DB giữa mỗi lượt. Thời gian SSO ghi riêng; D7 không tính thời gian nhập mật khẩu.
  Lượt lỗi/timeout được báo riêng và phải xử lý trước khi nhận gate, không âm thầm loại khỏi báo cáo.
- Nếu vượt 1,5 s: xem trace, payload và query trước; sửa chỗ đã đo là chậm. Không thêm cache/Redis,
  prefetch mọi trang hay tăng refresh interval để che bottleneck chưa xác định.

### 5.3 Lab backend ngừng hoạt động

Trước khi chạy, ghi số draft và bản ghi nghiệp vụ làm baseline. Sau fault, đối chiếu cùng các ID và
timestamp; không yêu cầu dự đoán của owner.

1. Xác nhận Mendix đang trỏ tới process `Nvm.App.Execution` đúng build; giữ Mendix/SQL/PostgreSQL/broker chạy.
2. Nhập và lưu một draft; chờ request lưu hoàn tất, kiểm draft tồn tại sau reload.
3. Chỉ dừng Execution. Submit, kiểm câu thông báo đúng ở §1, còn input/draft và chưa có business effect.
4. Khởi động lại Execution giữ DB. Gửi lại draft với key cũ; đối chiếu một business record và event D3.
5. Chạy case mất response sau commit trên môi trường test; retry xác nhận record count không tăng.
6. Ghi số draft trước/sau, business count, event ID/queue count, thời gian báo lỗi và phục hồi vào
   benchmarks/runbook; hoàn nguyên fault, kiểm service healthy. Không `down -v` hoặc xoá volume.

**Gate cuối:** đọc diff, so DoD/K1–K13/docs và chạy targeted checks trước. Còn finding actionable thì
sửa trong lượt được phép, kiểm lại; không chạy `buffer-crash` 200 vòng để dò lỗi UI. Chỉ khi sạch mới
`make ci`, kiểm network boundary của host vừa thêm. Audit độc lập cuối milestone có thể làm tuần tự
trong lượt riêng; không tự spawn sub-agent trái lựa chọn của owner.

## 6. Ranh giới và nợ có địa chỉ

| ID | Trạng thái / hệ quả phải nói rõ | Đóng ở đâu |
|---|---|---|
| N-M4-1 | Đã nối command vào SQL event store/outbox cùng transaction; build 0 warning/error. 48/48 HTTP + SQL outbox tests qua; gồm broker outage + process restart, rollback, replay, lease khi publisher chậm và header đúng envelope đã lưu | Review độc lập không còn finding trong các bản sửa này; còn xác nhận image demo và audit tổng thể N3 |
| N-M4-2 | Dispatch/WIP đã nối projection lifecycle; quality release/location movement và UI auto-refresh/status chưa nghiệm thu. Fixture context còn cho draft PoC cũ chưa gửi | M5/M6, ADR-043; kiểm runtime Mendix và đối soát draft trước khi bỏ fallback |

Không đưa event store/stream/replay engine (M5), outbox/projection framework (M6), channel → unit (M7),
grading/matching (M8), Quality workflow/override (M9), ERP (M11), offline sync toàn app hoặc k3d/Helm
hardening vào M4. K7, K3, auth và draft bền vững là phần cần thiết để hành vi đang hứa đúng trong thực tế.

Thời lượng hai tuần là dự kiến trong scope. OData PoC hoặc durable transaction tốn hơn dự kiến thì
báo lại bằng việc còn lại và bằng chứng; không bỏ DoD để giữ lịch. Hạng mục mới chỉ thêm khi gắn được
với một DoD hoặc ràng buộc hiện hành.

## 7. Checklist nghiệm thu

- [x] C01 đã chốt tài liệu; C02 có PoC/ADR-013; C03 đã có backend và import schema, commit cả hai repo. C04 đã commit cả hai repo; C05 commit `8639e75`. C06–C10 chưa nghiệm thu; M4 còn mở.
- [ ] D1–D8 và lab có bằng chứng; số chưa đo không được ghi như kết quả.
- [ ] Backend đúng image/code đang chạy, base URL đúng; Mendix build/check consistency không lỗi,
  Security Production và quyền được kiểm bằng tài khoản thật.
- [ ] Có RED/GREEN tái lập cho các test yêu cầu; targeted checks, full CI và network checks có kết quả.
- [ ] ADR-007/014/038 đã có; ADR-013 có bằng chứng C02; catalog/golden file/glossary/runbook đồng bộ với code cuối M4.
- [ ] Các việc còn nợ chỉ nằm trong bảng §6; nếu có file audit tạm được owner cho phép, chuyển kết luận
  vào docs chính và xoá file cùng mọi tham chiếu khi đóng findings.
- [ ] Ghi danh sách file/model document, kết quả kiểm chứng và giới hạn chưa kiểm được; agent commit/push backend sau kiểm tra, nhờ owner Commit and Push Mendix khi công cụ chưa hỗ trợ.

Chỉ đổi trạng thái M4 sau khi các gate kỹ thuật ở trên có bằng chứng. Các câu hỏi giải thích nghiệp vụ
được trả lời khi chủ repo yêu cầu, không ảnh hưởng nghiệm thu.
