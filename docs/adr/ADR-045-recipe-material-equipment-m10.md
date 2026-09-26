# ADR-045 — Recipe, Material, Equipment: luật nằm ở DB và ở base, không ở phần trăm

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-26 |
| **Liên quan** | `docs/scope.md` §5.6, §6.8, §6.9, §6.10, §9/M10 · ADR-017, ADR-044 |

---

## Context

M10 thêm ba FB hỗ trợ. DoD: DB (không phải code) chặn hai recipe version cùng Active; trả lời "lúc 14:20 ngày 12/3
máy COAT-01 chạy recipe nào" bằng version + hash; lot đã mở quá 4 giờ bị chặn với câu "4h32m / giới hạn 4h00m";
OEE hai line gộp lại khác trung bình cộng, kiểm bằng ground truth; người dùng NV1 thấy 0 dòng DE1.

## Decision

**Recipe**

- Version bất biến sau duyệt; `ContentSha256` tính từ nội dung tham số. Duyệt cần chữ ký QaManager + ProductionManager
  trên đúng hash, không ai là người soạn (dùng `ISignatureVerifier` của Quality qua Contracts).
- Filtered unique index `UX_RecipeVersions_OneActive (SiteId, EquipmentClass, ProductCode, StepCode) WHERE Status =
  'Active'`. Version cũ chuyển Superseded với `EffectiveTo` = `EffectiveFrom` của version mới, cùng transaction.
- `ApplyRecipe` chọn version hiệu lực tại `OccurredAt`, ghi event `RecipeVersionApplied` vào stream
  `equipment-recipe:{path}` và một dòng append-only `recipe.Applications`. Câu hỏi "lúc T máy X chạy gì" đọc bảng này.

**Material**

- Lot có vòng đời Received → Released (Quality) → Opened. Luật tiêu hao chạy trong transaction của
  `ConsumeMaterial` theo thứ tự: đơn vị, đã release, đủ số lượng, hạn dùng, thời gian tiếp xúc, FIFO. Mỗi luật trả mã
  và câu cụ thể; thời lượng in dạng `4h32m`.
- Hết hạn, quá tiếp xúc và FIFO vượt được bằng override có hạn, cần chữ ký QaManager khác người đề nghị. Đơn vị và
  số lượng không override được.
- Hold của Quality trên lot/đoạn cuộn chặn tiêu hao cho mọi loại lot (`IMaterialHoldCheck`).
- Bỏ event `MaterialLotExpired`: hết hạn là luật tính lúc tiêu hao từ `ExpiresAt`/`OpenedAt`, không phải một fact.

**Equipment**

- Máy có hai trạng thái Running/Stopped. Dừng phải có lý do là **lá** của cây lý do theo site; category của lá là
  Planned hoặc Unplanned.
- Khi máy chạy lại, lần dừng được đóng và phân loại theo độ dài thật: Planned giữ Planned; Unplanned < 5 phút là
  MicroStop; còn lại Unplanned. Event `EquipmentDowntimeRecorded` mang category đã phân loại.
- Sản lượng ghi theo khoảng; ideal cycle time (master data có version theo EquipmentClass × ProductCode) được chốt
  vào dòng sản lượng lúc ghi.
- OEE tính từ base: Planned = cửa sổ − dừng Planned; Run = Planned − dừng Unplanned (micro-stop vẫn nằm trong Run,
  nên chỉ làm giảm Performance); Ideal = Σ tổng × ideal cycle. Gộp nhiều máy = cộng base rồi tính lại.

**Multiplant**

- Mọi query đọc site từ token (`site_id`), không từ tham số. Test HTTP seed dữ liệu DE1 ở mọi read endpoint mới, kiểm
  token DE1 thấy và token NV1 không thấy.

## Consequences

**Được**

- Lách code để bật lại version cũ vẫn bị DB từ chối (lỗi 2601/2627).
- OEE đối chiếu được bằng tay: API trả base cùng tỉ lệ.
- Ideal cycle đổi sau này không làm đổi OEE của quá khứ.

**Mất / phải chịu**

- Sản lượng được tính vào cửa sổ theo `WindowTo`; khoảng đếm vắt qua biên cửa sổ bị tính trọn vào một phía. Máy báo
  khoảng ngắn thì sai lệch nhỏ; khoảng dài (ví dụ 4 giờ) thì OEE theo giờ lệch rõ.
- Lần dừng đang mở được phân loại theo độ dài **tới lúc hỏi**: một lần dừng 3 phút đang mở hiện là MicroStop, sau 5 phút
  thành Unplanned. OEE của cùng cửa sổ có thể đổi cho tới khi lần dừng đóng.
- Cây lý do seed bằng migration cho NV1/DE1; chưa có API sửa cây. Đổi category của một lá không sửa lại các lần dừng
  đã ghi.
- OEE tính trực tiếp trên bảng SQL của FB, chưa có continuous aggregate `rm.oee_by_shift` ở PostgreSQL (scope §8).
  Ổn với vài chục máy; báo cáo nhiều tháng cần read model.
- Recipe chưa gắn với từng unit; chỉ gắn với lần chạy (operation run / lot) trên máy. Trace từ cell về recipe đi qua
  operation run.

**Việc phát sinh**

- Mendix: màn duyệt recipe, lot với đồng hồ đếm ngược, dashboard OEE, và kiểm Mendix không thấy DE1.
- Read model OEE theo ca (production day/shift của ADR-012) khi có dashboard.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Chặn hai Active bằng `if` trong handler | DoD đòi DB chặn; hai request song song đều qua `if` |
| Exclusion constraint theo khoảng hiệu lực | SQL Server không có; filtered unique index trên Status đủ cho "một Active" |
| OEE = trung bình OEE từng máy | Sai khi máy chạy số giờ khác nhau; test ground truth lệch 0,058 |
| Tính micro-stop theo ngưỡng lúc dừng bắt đầu | Chưa biết độ dài lúc bắt đầu |

## Evidence

- `RecipeMaterialTests` 2/2: duyệt thiếu chữ ký → `MISSING_SIGNATURE`; người soạn ký → `SEPARATION_OF_DUTIES`;
  apply ngày 13/3 → v1, ngày 15/3 → v2; lookup 13/3 14:20 → v1 đúng hash; site DE1 → không có; UPDATE bật lại v1
  → SqlException 2601/2627. Lot mở 4h32m → `EXPOSURE_EXCEEDED` "Lot đã mở 4h32m / giới hạn 4h00m."; sai đơn vị →
  `UOM_MISMATCH`; override chỉ mở luật tiếp xúc, FIFO vẫn chặn và nêu lot cũ; hold lot → `LOT_ON_HOLD`.
- `EquipmentOeeTests` 1/1: line 1 (8 h, đổi sản phẩm 1 h, hỏng 30 phút, kẹt 3 phút, 20.000/19.000, ideal 1 s) OEE =
  19.000/25.200; line 2 (6 h không có lệnh, 7.000/6.930) OEE = 0,9625; gộp = 25.930/32.400 = 0,8003, trung bình cộng
  0,8582. `OeeCalculatorTests` 7/7 (biên 5 phút, cắt cửa sổ).
- `CrossSiteIsolationHttpTests` 1/1. Lab phá hoại: bỏ `SiteId` khỏi query aging/due → test này đỏ ở đúng assertion
  aging; bỏ query filter site của `ProductionUnits` trong `PomReadDbContext` → 7 test POM đỏ. Cả hai đã khôi phục.
