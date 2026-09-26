# Danh mục domain event

Mọi event có thể tồn tại trong hệ thống, và tình trạng thật của từng cái.

Nguồn: [`scope.md`](scope.md) §6.5. Bốn cột bên phải là phần thêm, chỉ tồn tại ở đây.

## Cách đọc

| Cột | Nghĩa |
|---|---|
| **Version** | Version schema hiện tại. Trống = chưa có event nào được viết |
| **Đã cài đặt** | Có `record` trong `Nvm.Contracts` với `[EventContract]` + `[EventVersion]` |
| **Golden file** | Có file JSON thật trong `tests/Contract/golden/`, và test đọc lại được |
| **Dự kiến** | Milestone dựng **Functional Block sở hữu** event đó (`scope.md` §9). Là ước lượng, không phải cam kết |

## Luật

1. **Tên ở thì quá khứ, theo ngôn ngữ nhà máy.** `ProductionUnitSerialized`, không phải
   `CreateUnitCommand`. Event là **sự thật đã xảy ra**, không phải ý định.
2. **Đã cài đặt thì phải có golden file.** Một event không có golden file là một event không ai
   chứng minh được sẽ đọc lại được sau 15 năm (`scope.md` §8.4).
3. **Thêm event mới → thêm dòng ở đây trong cùng commit.** Bảng lệch là bảng vô dụng.
4. **Version tăng theo luật `scope.md` §7.4**: thêm field optional → không tăng; đổi ý nghĩa / xoá
   field / đổi kiểu → tăng, và viết upcaster. Golden file cũ **không bao giờ sửa**, chỉ thêm file mới.


¹ **`MeasurementRecorded` — ai phát, và vì sao không phát mọi reading.**
Schema thuộc bounded context **Quality**; **Ingestion** là bên *phát* từ M2, còn Quality *nhận và
đánh giá* từ M5 (K8: đi qua contract + bus, không reference trực tiếp).

Ingestion **không** phát mọi phép đo. `scope.md` §5.5 vạch ranh giới: quan trắc liên tục là
**telemetry** và dừng ở TimescaleDB; giá trị **đã đánh giá** mới là domain event. Một row chỉ được
phát khi **đồng thời**: signal code nằm trong whitelist (`NVM_INGEST__PublishedSignals`) và có
`UnitId`. Whitelist chỉ chứa **`Formation/CapacityResult`** — tên riêng của kết quả cuối đã đánh giá.
Đường cong thô mang tên `Formation/Capacity`, một tên khác, nên nó **không bao giờ** đủ điều kiện dù
sau M7 mọi reading thô cũng sẽ có `UnitId`. `UnitId` là điều kiện *đầy đủ* của một event đã là fact,
không phải phép thử xem nó có phải fact hay không. Vì thế đường cong MQTT vẫn được lưu đầy đủ nhưng
không lên bus; C14
được chứng minh bằng fixture kết quả cuối từ CSV adapter có khai báo `UnitId` của cell. Ingestion
không xác thực phép ánh xạ cell từ channel ở M2 — việc đó thuộc formation process ở M7.

Whitelist mặc định **rỗng**. Phát mọi reading ở tốc độ N1 là 5.000 event/giây lên một bus dựng để
chở quyết định — và nó hỏng **âm thầm**: không có lỗi nào, broker chỉ đầy dần, và event store biến
thành đúng cái TSDB nằm cạnh nó.

---

## Bảng

| Event | FB phát ra | Ý nghĩa | Version | Đã cài đặt | Golden file | Dự kiến |
|---|---|---|---|---|---|---|
| **`FactoryModelRevisionActivated`** | **FactoryModel** | **Một revision của cây ISA-95 vào hiệu lực tại một nhà máy** | **v1** | **✅** | **✅** | **M1** |
| `MaterialLotReceived` | Material | Nhận lot từ nhà cung cấp | — | ☐ | ☐ | M10 |
| `MaterialLotReleased` | Quality | Lab đạt → cho phép dùng | — | ☐ | ☐ | M9 |
| `MaterialLotConsumed` | Material | Tiêu hao lot hoặc đoạn cuộn [from, to) vào một unit (cạnh TRANSFORMATION); stream `consumption:{serial}` | v1 | ✅ | ✅ | M6 |
| `MaterialLotExpired` | Material | Hết shelf life hoặc quá exposure | — | ☐ | ☐ | M10 |
| `SlurryBatchProduced` | ProductionExecution | Trộn xong một mẻ | — | ☐ | ☐ | M5 |
| `RollCoated` | ProductionExecution | Phủ xong, kèm segment map; stream `roll:{rollId}` | v1 | ✅ | ✅ | M6 |
| `RollSplit` | Traceability | Slitting: mother → daughter + ánh xạ toạ độ | — | ☐ | ☐ | M6 |
| `ProductionUnitSerialized` | Traceability | **Cell được cấp SN** — điểm khai sinh serial | v1 | ✅ | ✅ | M5 |
| `SerialEngravingVerified` | Quality | Vision đọc lại mã khắc thành công | — | ☐ | ☐ | M9 |
| `DuplicateSerialDetected` | Traceability | Trùng mã — quarantine + audit | v1 | ✅ | ✅ | M5 |
| `ProcessStepStarted` | Traceability ([ADR-042](adr/ADR-042-unit-event-ownership.md)) | Bắt đầu một bước | v1 | ✅ | ✅ | M5 |
| `ProcessStepCompleted` | Traceability ([ADR-042](adr/ADR-042-unit-event-ownership.md)) | Kết thúc operation run đã ghi nhận actual riêng | v1 | ✅ | ✅ | M5 |
| `DataCollectionRecorded` | ProductionExecution | Kết quả do người vận hành nhập đã được ghi nhận; không kết luận hoàn tất bước hay đạt chất lượng ([ADR-038](adr/ADR-038-data-collection-thu-cong-o-m4.md)) | v1 | ✅ | ✅ | M4 · C06 |
| **`MeasurementRecorded`** | **Quality** | **OCV, ACIR, torque, áp suất hàn… — giá trị ĐÃ ĐÁNH GIÁ, không phải đường cong thô** | **v1** | **✅** | **✅** | **M2** ¹ |
| `FormationRunStarted` | ProductionExecution | Vào máy formation, gắn tray/channel | — | ☐ | ☐ | M7 |
| `FormationRunCompleted` | ProductionExecution | Xong, kèm summary + URI đường cong | — | ☐ | ☐ | M7 |
| `AgingPeriodElapsed` | ProductionExecution | Saga timeout — đủ ngày aging | — | ☐ | ☐ | M7 |
| `UnitGraded` | Grading | Gán bin sau grading | — | ☐ | ☐ | M8 |
| `UnitAssembledInto` | Traceability | cell → module, module → pack; stream `membership:{con}` | v1 | ✅ | ✅ | M6 |
| `UnitRemovedFrom` | Traceability | Rework: tháo ra | v1 | ✅ | ✅ | M6 |
| `UnitQuarantined` | Quality | Bị giữ; M5 facet: serial trùng giữ unit trong cùng transaction | v1 | ✅ | ✅ | M9 |
| `UnitReleasedFromQuarantine` | Quality | Được thả, kèm 2 chữ ký | — | ☐ | ☐ | M9 |
| `UnitScrapped` | Quality | Loại bỏ | — | ☐ | ☐ | M9 |
| `NonConformanceRaised` | Quality | Mở NCR | — | ☐ | ☐ | M9 |
| `DispositionApplied` | Quality | MRB ra quyết định | — | ☐ | ☐ | M9 |
| `HoldCascadeStarted` | Quality | Bắt đầu job lan hold hạ nguồn | — | ☐ | ☐ | M9 |
| `HoldCascadeCompleted` | Quality | Job lan hold xong | — | ☐ | ☐ | M9 |
| `RecipeVersionApproved` | Recipe | Duyệt, kèm e-signature | — | ☐ | ☐ | M10 |
| `RecipeVersionApplied` | ProductionExecution | **Ghi lại version nào đã dùng cho lot nào** | — | ☐ | ☐ | M10 |
| `EquipmentStateChanged` | Equipment | Chạy / dừng / bảo trì | — | ☐ | ☐ | M10 |
| `EquipmentDowntimeRecorded` | Equipment | Dừng có lý do | — | ☐ | ☐ | M10 |
| `WorkOrderReleased` | ProductionExecution | ERP đẩy xuống | — | ☐ | ☐ | M11 |
| `ProductionEolTestPassed` | Quality | Test cuối chuyền đạt | — | ☐ | ☐ | M9 |
| `PassportPublished` | Passport | DPP được công bố | — | ☐ | ☐ | M12 |
| `GenealogyCorrectionRecorded` | Traceability | Bút toán bù trừ: thay cha đã ghi sai (cạnh CORRECTION) | v1 | ✅ | ✅ | M6 |

---

## Trạng thái hiện tại — sau implementation M3

| | Số |
|---|---|
| Đã cài đặt | **2** / 36 |
| Có golden file | **2** / 2 đã cài đặt |
| Chưa cài đặt | 34 |

M1 kết thúc với **1/35** (`FactoryModelRevisionActivated`). M2 thêm `MeasurementRecorded` v1 nhưng
chỉ phát các signal đã được đánh giá, không biến mọi telemetry reading thành domain event. M3 cố ý
không thêm event mới: hypertable, rollup và production calendar là persistence/read model, không phải
một sự kiện nghiệp vụ mới.

> [!note] Khoảng trống của scope đã được đóng
> `FactoryModelRevisionActivated` được thêm ở M1/C03 vì việc cấu hình nhà máy đổi revision là một
> sự kiện thật: một work cell bị gỡ trong khi vẫn còn WIP có thể dẫn tới hold ở M9. `scope.md` §6.5
> nay đã liệt kê event này thay vì để catalog và scope trôi khỏi nhau.

`DataCollectionRecorded` v1 đã cài đặt ở C06, golden tại `tests/Contract/golden/production-execution/`.
Event ID bằng key suy ra từ submission; `occurredAt` từ lần xác nhận nhập, `recordedAt` từ server.
`value` là JSON number, giữ đủ độ chính xác decimal. Event/outbox commit cùng business record;
worker publish sau commit và retry với cùng ID. Command replay/rejection không tạo publish intent mới.
Delivery vẫn at-least-once; consumer chống trùng theo event ID.
Viết ADR không làm tăng số event đã cài đặt; các ô chỉ được đánh dấu khi có code và bằng chứng tương ứng.

UnitMeasurementRecorded (Traceability, v1): kết quả process được chấp nhận; không suy ra quality hay hoàn tất bước. Đã có contract và golden trong tests/Contract/golden/traceability. Năm golden Traceability là fixture contract cố định, không phải bản chụp nguyên văn của phiên runtime. Ownership theo ADR-042.
