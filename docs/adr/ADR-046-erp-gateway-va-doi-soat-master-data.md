# ADR-046 — ERP gateway: không đoán mã, không quy đổi, không mất lệnh

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-26 |
| **Liên quan** | `docs/scope.md` §7.6, §9/M11 · ADR-010, ADR-023, ADR-045 |

---

## Context

ERP (NovaERP stub) thả file B2MML `ProductionSchedule` vào thư mục SFTP. Dữ liệu cố ý bẩn: một vật liệu có bốn mã
(`MAT-0009812`, `9812`, `MAT9812`, `NMC811-CAM`), mã sản phẩm có khoảng trắng thừa, vật liệu chưa tồn tại, gram thay vì
kg, cùng lịch gửi hai lần, file sai schema. DoD M11: bốn mã về một canonical id; file sai schema vào `rejected/` kèm
`.error.txt` mà watcher không chết; mã lạ → `PendingMasterData` + task, không mất lệnh; cùng file hai lần → một work
order; lệch đơn vị bị phát hiện, không quy đổi âm thầm. Chiều lên: backflush tiêu hao về ERP theo lô 5 phút.

## Decision

- **FB MasterData** giữ mã chuẩn (vật liệu có đơn vị gốc), alias ERP → mã chuẩn và task đối soát. Chuẩn hoá chỉ bỏ
  khoảng trắng hai đầu và đổi chữ hoa. Mã khác dạng (`9812`) phải được ánh xạ tường minh, có lý do và người làm; ánh xạ
  lại alias đang dùng bị từ chối (`ALIAS_CONFLICT`). Khai báo mã chuẩn hoặc ánh xạ alias đóng các task "mã lạ" của mã
  đó. Lệch đơn vị chỉ đóng bằng "chấp nhận" có ghi chú, cho đúng chứng từ đó.
- FB khác dùng master data qua port `IMasterDataReconciliation` trong Contracts, cùng transaction của command.
- **Work order thuộc ProductionExecution**. `ReceiveWorkOrder` có submission `{ScheduleId}/{WorkOrderId}` (ADR-010):
  file gửi lại phát lại kết quả cũ; cùng ID nhưng nội dung khác bị từ chối như xung đột định danh. Lệnh luôn được ghi
  (`WorkOrderReceived`); thiếu master data thì `PendingMasterData` kèm id task, đủ thì thêm `WorkOrderReleased`. Số
  lượng và đơn vị ERP gửi được giữ nguyên.
- Mỗi thay đổi master data tăng **revision** của site. `ReevaluateWorkOrder` có submission theo revision, nên mỗi lệnh
  chờ được thử đúng một lần cho mỗi thay đổi; poll khi không có gì đổi chỉ phát lại kết quả.
- **Nvm.ErpGateway** (thư viện ở `src/Integration`, chạy như hosted service trong Execution khi `NVM_ERP:Enabled`):
  đọc file `*.xml` theo thứ tự tên, mở độc quyền (file đang ghi để lượt sau), kiểm XSD tập con tự viết của B2MML V0600
  (không tải DTD/thực thể ngoài). File hỏng hoặc xung đột → `rejected/` + `.error.txt` có dòng/cột; lỗi hạ tầng → giữ
  file ở `inbound/` và dừng lượt; xong → `processed/`.
- **Backflush**: đọc `MaterialLotConsumed` đã commit theo `GlobalSequence`, gộp theo vật liệu/lot/đơn vị. Khoảng sequence
  và `BatchId` (UUIDv5 theo khoảng) được chốt vào `erp.BackflushCheckpoints` trước khi POST; gửi lại đúng lô đó tới khi
  ERP trả 2xx rồi mới tiến checkpoint. ERP bỏ lô trùng theo header `Idempotency-Key`.

## Consequences

**Được**

- Không có đường nào để một mã lạ hay một đơn vị lệch lọt vào sản xuất mà không có người ký tên.
- Watcher tuần tự xử lý 1.000 file trong một lượt, 35,99 s, không cần hàng đợi ở tải ERP thực tế (vài chục lịch/ngày).
- Mất phản hồi của ERP không tạo lô chồng: lần gửi sau mang đúng khoảng và `BatchId` cũ.

**Mất / phải chịu**

- Gateway chạy trong process Execution, không phải process riêng như scope viết. Một lượt 1.000 file giữ vòng lặp 36 s,
  trong lúc đó đánh giá lại và backflush phải chờ. Tách process chỉ là cấu hình host, chưa làm.
- XSD là tập con tự viết, không phải XSD chính thức của MESA. File hợp lệ theo MESA nhưng có phần tử lạ trong namespace
  B2MML ở cấp schedule/request/segment sẽ bị từ chối.
- Backflush gộp theo lot, không theo work order: `MaterialLotConsumed` không mang work order. ERP muốn backflush theo lệnh
  thì cần thêm work order vào event tiêu hao (version mới của event).
- Một site một gateway (`NVM_ERP:SiteId`); B2MML không mang site.
- Không có SFTP server trong compose: thư mục inbound là volume dùng chung; SFTP là hạ tầng của ERP.
- Chấp nhận lệch đơn vị không đổi số lượng: 480.000 g vẫn là 480.000 g trong work order. Ai tiêu hao theo kg phải biết
  điều đó; task mang câu chi tiết để kho xác nhận.

**Việc phát sinh**

- Mendix: màn Reconciliation (danh sách task, ánh xạ thủ công, chấp nhận có ghi chú).
- Tách gateway thành process riêng khi có deploy M13.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Chuẩn hoá thông minh (bỏ tiền tố `MAT-`, bỏ số 0) | Đoán sai là gán nhầm vật liệu vào pin; scope đòi fail loud |
| Tự quy đổi kg ↔ g | Scope cấm; lệch đơn vị thường là lỗi dữ liệu ERP, không phải lỗi hiển thị |
| Từ chối cả lệnh khi thiếu master data | Mất lệnh; DoD đòi `PendingMasterData` |
| Hàng đợi (RabbitMQ) giữa watcher và command | Lab 1.000 file cho thấy tuần tự đủ; thêm hạ tầng mà không có tải cần |
| Checkpoint backflush chỉ tiến sau 2xx, không chốt lô | Mất phản hồi + event mới tới → lô sau chồng lô trước |

## Evidence

- `ErpGatewayTests` 2/2: 9 file → 7 xử lý, 2 từ chối (số lượng `abc`, không phải XML) có `.error.txt`; 4 mã về
  `MAT-0009812`, `"NV-P120-NMC "` giải được; lịch PS-0834 hai lần → 6 work order cho 7 file hợp lệ; `MAT-0009999` và
  gram/kg → `PendingMasterData` + 2 task (câu "ERP gửi 480000 g cho MAT-0009812, đơn vị gốc là kg."); poll cùng revision
  → 0 lệnh; khai báo vật liệu → 1 lệnh phát xuống; chấp nhận task đơn vị → 1 lệnh, số lượng vẫn 480.000 g; lịch cùng ID
  nội dung khác → rejected. Backflush: ERP trả 500 → không tiến; lần hai 200 cùng `BatchId`; lần ba không gửi; dòng lot
  `ROLL-BF` = 1,64 m từ 2 lần tiêu hao.
- `B2mmlParserTests` 5/5, gồm DOCTYPE/thực thể ngoài bị từ chối.
- `ErpInboundLabTests` (NVM_RUN_LABS=1), một lần chạy: 1.000 file, 1.000 xử lý, 35,99 s (28 file/s), mỗi file một
  durable command trên SQL Server Testcontainers.
