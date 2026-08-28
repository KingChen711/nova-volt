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
| `MaterialLotConsumed` | Material | Tiêu hao trong một operation | — | ☐ | ☐ | M10 |
| `MaterialLotExpired` | Material | Hết shelf life hoặc quá exposure | — | ☐ | ☐ | M10 |
| `SlurryBatchProduced` | ProductionExecution | Trộn xong một mẻ | — | ☐ | ☐ | M5 |
| `RollCoated` | ProductionExecution | Phủ xong, kèm segment map | — | ☐ | ☐ | M5 |
| `RollSplit` | Traceability | Slitting: mother → daughter + ánh xạ toạ độ | — | ☐ | ☐ | M6 |
| `ProductionUnitSerialized` | Traceability | **Cell được cấp SN** — điểm khai sinh serial | — | ☐ | ☐ | M5 |
| `SerialEngravingVerified` | Quality | Vision đọc lại mã khắc thành công | — | ☐ | ☐ | M9 |
| `DuplicateSerialDetected` | Traceability | Trùng mã — luồng ngoại lệ có thật | — | ☐ | ☐ | M5 |
| `ProcessStepStarted` | ProductionExecution | Bắt đầu một bước | — | ☐ | ☐ | M5 |
| `ProcessStepCompleted` | ProductionExecution | Kết thúc, kèm actual | — | ☐ | ☐ | M5 |
| **`MeasurementRecorded`** | **Quality** | **OCV, ACIR, torque, áp suất hàn… — giá trị ĐÃ ĐÁNH GIÁ, không phải đường cong thô** | **v1** | **✅** | **✅** | **M2** ¹ |
| `FormationRunStarted` | ProductionExecution | Vào máy formation, gắn tray/channel | — | ☐ | ☐ | M7 |
| `FormationRunCompleted` | ProductionExecution | Xong, kèm summary + URI đường cong | — | ☐ | ☐ | M7 |
| `AgingPeriodElapsed` | ProductionExecution | Saga timeout — đủ ngày aging | — | ☐ | ☐ | M7 |
| `UnitGraded` | Grading | Gán bin sau grading | — | ☐ | ☐ | M8 |
| `UnitAssembledInto` | Traceability | cell → module, module → pack | — | ☐ | ☐ | M6 |
| `UnitRemovedFrom` | Traceability | Rework: tháo ra | — | ☐ | ☐ | M6 |
| `UnitQuarantined` | Quality | Bị giữ | — | ☐ | ☐ | M9 |
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
| `GenealogyCorrectionRecorded` | Traceability | Bút toán bù trừ | — | ☐ | ☐ | M6 |

---

## Trạng thái sau M1

| | Số |
|---|---|
| Đã cài đặt | **1** / 35 |
| Có golden file | **1** / 1 đã cài đặt |
| Chưa có gì | 34 |

**Một dòng, và đó là con số đúng.** M1 dựng *đường ống*, không dựng nghiệp vụ. Event duy nhất tồn
tại là thứ nhỏ nhất đủ để chứng minh đường ống chạy: bus chở được nó, hai consumer nhận độc lập, và
lab phá hoại đếm được bao nhiêu cái mất khi broker chết.

> [!note] `FactoryModelRevisionActivated` **không** có trong `scope.md` §6.5
> Nó được thêm ở M1/C03 vì `scope.md` §6.5 liệt kê event của *sản phẩm* — cell, lot, pack — và
> không có event nào cho việc **cấu hình nhà máy thay đổi**. Đó là một khoảng trống thật của scope,
> không phải một event bịa ra cho đủ: một work cell bị gỡ khỏi cây trong khi vẫn còn WIP đứng trên
> nó là tình huống có thật, và nó dẫn thẳng tới bài toán hold ở M9.
>
> Cập nhật `scope.md` §6.5 khi tiện — không cần commit riêng.

> [!warning] Bảng này sẽ đứng yên rất lâu, và đó là dự kiến
> 34/35 event thuộc M5–M12. Trước M5 bảng gần như không nhúc nhích. Đừng vì thế mà tưởng dự án
> đứng — cùng lý do đã ghi ở cuối `oef-mapping.md`.
