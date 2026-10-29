# ADR-058 — Thành phẩm: vật chứa có trạng thái ở lớp Product, đóng gói theo bảng cấu hình, ShipApp và YardApp, procedure gọi qua kết nối riêng

| | |
|---|---|
| **Status** | **Accepted** — agent quyết theo AGENTS.md §2.3, chờ chủ repo đọc; mục 5 chủ repo chốt 2026-10-29: sửa câu K9 |
| **Date** | 2026-10-29 |
| **Liên quan** | ADR-056 · ADR-057 · scope §5 (R7) · [bảng cơ chế](../oef-mapping.md#r7-đóng-gói-cất-kho-trailer-kho-kiểm-pallet) |

## Context

R7 đưa thành phẩm từ cuối line vào kho:

- Trạm đóng tote ghép hai module thành kit (họ A) hoặc cho module vào tote (họ B); trạm xếp pallet nạp đủ tote lên pallet.
  Trên hệ thật, một bảng cấu hình của team đánh dấu bước nào đóng tote, bước nào xếp pallet; post-action của team đếm lại
  tote, pallet sau mỗi lần nạp và ghi ngày đóng pallet. Trạng thái pallet (Empty, Full, Picked, Staged, Loaded) là state
  machine dạng dữ liệu của team, gán cho vật chứa của sản phẩm.
- Cất kho đi qua lệnh chuyển vật chứa của sản phẩm; post-action của team trên lệnh đó kiểm bước đo cuối rồi phát event,
  handler của event ghi bản ghi FIFO. Đề xuất lấy hàng, kiểm pallet, kiểm hàng lỗi khi nạp trailer kho là stored
  procedure: code của team mở kết nối SQL riêng bằng một configuration key và gọi procedure theo tên, ngoài platform.
  Procedure là phần cài tay, gói deploy không cài (lab L17); thiếu key thì màn dùng procedure hỏng (L18).
- Trailer kho thuộc app hậu cần bên ngoài của team, cùng chỗ với lịch xe và kéo xe (R9).

Lớp Product của R3 có `Mtua` nhưng không có trạng thái, số lượng, material, cũng không có lệnh chuyển vật chứa.

## Decision

1. **Lớp Product: vật chứa có trạng thái, số lượng, material.** `Mtua` thêm `StateMachine`, `Status`, `Quantity`,
   `MaterialNId`. Lệnh mới: `SetMtuaStatus` (theo transition dạng dữ liệu), `SetMtuaStatusForced`, `UpdateMtua` (đặt giá
   trị mới, không đếm), `MoveMtua` (đặt ở equipment, gỡ khỏi vật chứa cha), `DeleteMtua` (xóa mềm khi rỗng). Thêm
   `MaterialProperty`, `SetMaterialProperty`. Lớp Product không tự đếm: đếm là việc của post-action phía dự án.
2. **Đóng gói theo bảng cấu hình** (`PackingOperation`, FB của team): mã bước phía xưởng → `Tote`, `Pallet` hoặc `Test`
   (bước đo cuối gỡ tote khỏi pallet). Bước có trong bảng đối chiếu bước thì vẫn ghi WO rồi đóng gói (kit họ A); bước không
   có thì chỉ đóng gói (đóng tote họ B, xếp pallet). Tin mang mã vật chứa ở trường `container`, tin xếp pallet mang danh
   sách tote ở `contents`. Post-action `UpdatePalletBuild` trên lệnh gắn vào vật chứa đếm tote, pallet, gói lẻ và ghi
   `BuildDate`; gắn MTU thẳng vào pallet có tote thì tháo hết tote trước.
3. **Hai app mới của team.** `ShipApp` + `FB_OP_Ship`: cất kho, bản ghi FIFO (`PalletFifoOnMove` hẹn event
   `PalletLocated`, `PalletFifoWriter` ghi), kiểm pallet, gỡ pallet, gói lẻ, timer hết hạn đo. `YardApp` + `FB_OP_Yard`:
   trailer, lịch chất hàng của trailer kho, yêu cầu kéo xe (phần R9 sẽ thêm lịch xe, placard, nhận skid). Địa chỉ part ở
   `MapApp`. Mỗi app một bộ quyền gọi command theo role (`shipping`, `quality`, `yard`).
4. **Master data thêm bằng suite mới**, không sửa CSV của suite đã chạy: môi trường đã deploy có tên suite cũ trong lịch
   sử nên sẽ bỏ qua phần sửa (cùng cơ chế với L07). Đường poll của các mã bước mới cũng là suite mới (`line-polling`).
5. **Procedure gọi qua kết nối riêng của code dự án.** Lớp Project reference `Microsoft.Data.SqlClient`, đọc chuỗi kết nối
   từ key `Nv.SqlConnection` (đặt bằng bước tay `make sql-key`), gọi procedure theo tên. Procedure và index của nó cài cùng
   bước SQL tay với view (`make views`). Đây là cùng hình dạng với kết nối LineDB của R5 (Project reference Npgsql). K9
   đã sửa để cho phép đúng hai chỗ này (xem Consequences).
6. **Reading function ném lỗi lúc gọi**: service layer trả HTTP 500 mã `ReadingFunctionFailed` kèm câu lỗi (trước đây lỗi
   không có thân). Khác L15: lỗi xảy ra trước khi ghi dòng nào nên còn trả được mã lỗi.

## Consequences

**Được**

- Chuỗi tin của một line ra pallet Full đúng số tote, cả hai họ; tin xếp pallet tới sớm chờ gửi lại (−6500).
- Lab L03, L17, L18 và hai lab của bài cất kho (cắt chuỗi ở procedure, dỡ pallet cuối) có test tái hiện và đối chứng.
- Kiểm hàng lỗi đệ quy p95 636–758 ms (ba lần đo) trên 919 nghìn MTU, 2,23 triệu dòng genealogy (benchmarks); không có index thì một lần
  kiểm vượt 120 s.

**Mất / rủi ro**

- Mục 5 trước đây lệch câu chữ của K9 (ràng buộc cứng). Chủ repo chọn sửa K9 (2026-10-29): handler chỉ dùng `IPlatform` để đọc, ghi model; kết nối ra hệ ngoài hoặc SQL tay mà code thật cũng mở được dùng driver, chuỗi kết nối từ endpoint hoặc key. Phương án bị loại: chuyển gọi procedure vào Foundation (mất đường chẩn đoán "key kết nối thiếu" của L18, khác hình dạng code thật).
- Đo cuối dựng bằng tin từ LineDB như bài thực hành, không dựng màn phiên đo của hệ thật (phiên đo, kit không quét thành
  NC).
- Bản ghi FIFO và đề xuất chưa có đơn bán: picking, staging (R8) dùng lại chúng.

## Alternatives

| Phương án | Vì sao không chọn |
|---|---|
| Để team tự giữ trạng thái pallet trong facet, lớp Product không có trạng thái vật chứa | Trên hệ thật vật chứa của sản phẩm có trạng thái và số lượng; team chỉ khai state machine |
| Mọi tin đóng gói đều ghi WO (một process "đóng gói" cho mỗi line) | Thêm 8 WO mỗi ca chỉ để giữ một bước không làm ra sản phẩm; nguồn chưa đủ để biết WO đó dùng vào việc gì |
| Đọc cây thành phần bằng reading model (view) thay cho procedure | Mất L17, L18 và hình dạng code thật; đệ quy 13 tầng vẫn cần SQL tay |
