# ADR-038 — M4 ghi nhận kết quả đo thủ công trên context fixture

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-07 |
| **Liên quan** | [scope.md](../scope.md) §6.5/§7.2/§7.5/§9/M4 · [plan M4](../plans/M4-mendix-operator-station.md) C01/C03/C05/C06 · [ADR-010](ADR-010-idempotency-key-uuid-v5.md) · [ADR-014](ADR-014-mendix-ui-va-draft-ben-vung.md) · [ADR-023](ADR-023-claim-truoc-khi-chay-handler.md) |

## Context

Scope M4 yêu cầu submit kết quả đo, quan sát event và replay không ghi lần hai sau restart.
M2/M3 hiện chỉ sinh telemetry; chưa có ProductionUnit hay WIP read model như câu cũ trong scope giả định.
Ví dụ `complete-step` cũng cần completion engine của M5. Nếu dùng một event completion cho một form
chỉ lưu số, hồ sơ sẽ khẳng định sản phẩm đã xong công đoạn mà không có quyết định nghiệp vụ tương ứng.

ADR-001/002 đã chọn SQL Server cho write model và PostgreSQL cho read model. ADR-023 yêu cầu claim,
business effect và outcome cùng transaction từ M4; ADR-022 vẫn để outbox tới M6. Giữ các ranh giới đó.

## Decision

### Kết quả được ghi nhận

Dùng `RecordDataCollection` tại `POST /api/v1/commands/production/record-data-collection`, phát
`DataCollectionRecorded` v1 thuộc ProductionExecution. M4 nhập **điện áp pack** ở step code `EOL`
(End-of-Line), `signalCode=PackVoltage`, giá trị số thập phân, `unitOfMeasure=V`.

Envelope giữ `idempotencyKey`, `siteId`, `occurredAt`, `payload` như scope §7.5. Payload có:
`submissionId`, `serial`, `operationRunId`, `stepCode`, `equipmentPath`, `signalCode`, `value`,
`unitOfMeasure`. Actor lấy từ principal; `recordedAt` từ `TimeProvider`. `occurredAt` là thời điểm
người dùng xác nhận nhập trong Mendix, không giả tạo device/gateway timestamp.

Command nhận một fact nhập tay, không đổi execution/quality/location state, không phát
`ProcessStepCompleted`, `ProductionEolTestPassed` hoặc `MeasurementRecorded`. Chưa có giới hạn chất lượng
cho phép đo này, nên một giá trị được lưu không được gán nhãn "pack đạt".

### Context và quyền thực hiện

Seed **1.000 unit/site** NV1/DE1 từ cùng một fixture xác định được: context ghi trong SQL Server và
snapshot POM trong PostgreSQL. Fixture có pack tại P1/EOL cùng trường hợp bị chặn; mọi serial qua
validator hiện có. Trạm `NOVAVOLT/{site}/PACK/P1/EOL-01` lấy từ catalog revision 3 đã có.
Đây là resource ở cấp WorkCell, không tự bịa thêm một equipment con để đủ sáu cấp.

Seed chạy tường minh, không trong startup Production; lặp lại không ghi đè kết quả đã nhận.
Context tham chiếu đúng revision đã chọn của catalog; không phụ thuộc active revision đang ở RAM
để chứng minh dữ liệu bền vững. Đây không phải cơ chế activation bền vững của M5.

Backend kiểm mới trước khi ghi:

- User có `Operator` hoặc `LineLeader`, token/site hợp lệ; `siteId` trong request khớp principal.
- Serial là pack tồn tại trong context của site; operation run thuộc pack, step là `EOL`, resource
  khớp trạm được giao. Không nhận kết quả cho operation run của unit khác dù cùng một máy.
- Operation run đang `Running`; pack không `Held` hoặc `Scrapped` trong context sản xuất M4.
  Thu thập dữ liệu phục vụ điều tra hàng đang hold thuộc luồng Quality sau này, không phải quyền override ở form này.
- Signal/đơn vị đúng contract và giá trị là số biểu diễn được. Không tự suy ra ngưỡng đạt chất lượng.

POM chỉ hiển thị khả năng thao tác; backend kiểm lại trên nguồn context SQL trong transaction khi ghi.
Snapshot có thể cũ. Auto-refresh WIP chỉ đọc lại fixture; submit không làm số unit chuyển bước.
Collection POM chỉ có `@odata.nextLink` khi còn trang tiếp; trang cuối kết thúc paging. Đây là ngữ nghĩa
đọc dữ liệu, không phải bằng chứng connector/EDM đã chạy được ở C02.
M5/M6 thay nguồn bằng write model/projection thật và giữ bản ghi đã nhận, không xoá dữ liệu để seed lại.

### Danh tính submission và outcome

`submissionId` là UUID tạo một lần khi tạo draft. Khoá UUIDv5 dùng namespace hiện có và tuple
`(siteId, "RecordDataCollection", submissionId)` với cách mã hoá của `IdempotencyKey.FromNaturalKey`;
chuỗi UUID ở dạng `D` viết thường. Backend kiểm lại key. Đây là natural key **mới cho command nhập tay**;
không đổi key của measurement/process step cũ hay thuật toán ADR-010.

Lưu site, actor, command type, fingerprint của toàn bộ nội dung nghiệp vụ đã đóng băng và outcome.
Query luôn scope site. Cùng key nhưng actor/payload khác trả conflict; token hết quyền không được replay
outcome. Fingerprint bỏ transport metadata có thể đổi khi gửi lại, nhưng giữ `occurredAt` và payload.
Không có bằng chứng rằng chữ ký `IIdempotencyStore` hiện tại đã đủ mang metadata này; C05 chọn cách
truyền context trong infrastructure trước khi code, giữ `Claim/Complete/Abandon` nếu đủ dùng.

Claim + kết quả đo + outcome thành công commit trong **một SQL Server transaction**. Rejection nghiệp vụ
đã quyết định cũng lưu outcome, không có bản ghi kết quả đo/event; replay trả cùng rejection. Auth/input
sai trước claim và lỗi hạ tầng làm rollback không được biến thành outcome thành công hoặc claim treo.
Muốn thử một ý định mới sau rejection thì tạo submission mới tường minh; timeout vẫn retry submission cũ.

Response giữ shape scope §7.5. `accepted=true` nghĩa là kết quả đo đã commit, không nghĩa là pack đã đạt.
`accepted=false` kèm lý do/hành động có thực: sửa nháp mới, chọn lại việc hoặc quay về danh sách. Không
đề nghị override/release khi M4 chưa có chức năng đó. Lỗi transport không giả làm business rejection.

`DataCollectionRecorded` có `[EventVersion(1)]` và golden file khi implement; `EventId = ce_id = key`.
Chỉ nhánh xử lý mới publish **sau commit**, replay không publish lại. Lỗi publish được log/đếm;
DB đã commit thì không trả lời như đã rollback. Cửa sổ SQL commit → bus vẫn có thể mất event tới M6,
theo [ADR-022](ADR-022-publish-truc-tiep-khong-outbox-o-m1.md); M4 không tuyên bố N3 đã đạt.

## Consequences

**Được**

- D3–D5 có một business effect nhỏ nhưng thật để kiểm transaction/restart; event mô tả đúng điều xảy ra.
- Fixture cho UI đi trước event store/projection mà không cần suy diễn channel → unit từ telemetry.

**Mất / phải chịu**

- POM và context là dữ liệu demo. WIP không phản ánh sản xuất thật; phải ghi rõ giới hạn đó khi trình diễn.
- Có một contract event riêng cần bảo toàn và thêm natural key cho input thủ công; không tái sử dụng tên
  event chỉ để giảm số class. Phải kiểm UUIDv5 giữa Mendix/.NET và conflict payload/actor.
- Hai store fixture có thể lệch nếu seed sai; kiểm cùng nguồn và so key/context là trách nhiệm C03/C06.
  Hai DB này chưa có projection tự sửa lệch, và C01 chưa đo tính đúng đắn của fixture chưa được viết.
- Claim bền vững chưa bảo đảm bus nhận event. Rejection đã lưu cũng cần submission mới để thử lại sau
  khi điều kiện thay đổi; UI phải phân biệt rejection với timeout để người dùng không tạo ghi trùng.

**Việc phát sinh:** bổ sung event vào catalog ở trạng thái chưa cài đặt; C03 seed POM, C05 thêm SQL
transaction/context từ cùng fixture, C06 implement command/event. ADR-013 vẫn được chốt ở C02 bằng PoC
connector thật; quyết định fixture ở đây không giả định metadata đã tương thích Mendix.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Dùng `ProcessStepCompleted` hoặc `ProductionEolTestPassed` | Khẳng định hoàn tất/đạt chất lượng trong khi chưa có logic quyết định điều đó |
| Dùng `MeasurementRecorded` với timestamp thiết bị giả | Lệch ý nghĩa event đã đánh giá của Quality và nhầm nguồn số liệu nhập tay với luồng thiết bị |
| Kéo event store và projection vào M4 | Tăng phụ thuộc và phạm vi dù tám DoD chỉ cần một effect bền vững và read model demo |
| Ghi command ở PostgreSQL cho tiện | Lệch quyết định SQL Server write model, không cần thiết để đạt M4 |
| Đưa giá trị đo vào key để tránh lưu fingerprint | Sửa payload sẽ đổi key và có thể tạo bản ghi mới sau timeout; không phát hiện một key bị dùng sai |

## Evidence

- Đã đọc kernel idempotency, catalog event và scope tại `be05e43`: store vẫn in-memory;
  `ProcessStepCompleted` chưa implement; `MeasurementRecorded` đã có từ M2. Không coi các contract
  vừa chốt là code đang chạy.
- [Factory model r3](../../deploy/seed/factory-model.r3.json) có `PACK/P1/EOL-01` tại NV1 và DE1;
  [SerialNumber](../../src/Platform/Nvm.Kernel/Identity/SerialNumber.cs) nhận cấu trúc serial pack cần dùng.
- Ba tình huống review ở plan M4 §2.3 là ví dụ contract, **chưa chạy runtime**. Bằng chứng transaction,
  event, replay và UI thuộc C05–C09. Owner yêu cầu thực hiện C01 theo plan ngày 2026-09-07.
