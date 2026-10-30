# ADR-059 — Xuất hàng: gửi tin ra ngoài ở Foundation, đơn bán và chuyến giao trên ShipApp, ngoại lệ theo thứ tự, xe của khách trên YardApp

| | |
|---|---|
| **Status** | **Accepted** — agent quyết theo AGENTS.md §2.3, chờ chủ repo đọc |
| **Date** | 2026-10-30 |
| **Liên quan** | ADR-055 · ADR-058 · scope §5 (R8) · [bảng cơ chế](../oef-mapping.md#r8-xuất-hàng) |

## Context

R8 đưa pallet thành phẩm từ kho tới xe của khách:

- ERP gửi đơn bán (JSON) và nhận Ack từng đơn. Đơn gom vào nhóm theo nơi nhận, route, giờ giao, bỏ nhóm đã giao; nhóm mới
  kèm một chuyến giao. Họ khách nhận theo mã hãng vận tải của đơn: họ A ghép route từ nơi nhận và giờ giao, họ B lấy route của
  khách trong đơn, trống thì tra dữ liệu đặt hàng của khách.
- Picking theo FIFO (ngày đóng pallet) giữ pallet cho đơn mà không đổi vị trí; lấy lệch đề xuất không bị chặn. Staging xác
  nhận pallet đã tới khu tập kết: chỉ nhận pallet đã lấy cho nhóm này, đổi vị trí. Họ A dùng nhãn skid in sẵn, họ B dùng nhãn
  hai mặt và quét từng nhãn hộp.
- Chất xe họ B gửi hai tin **đồng bộ** tới hệ của khách và chỉ chất khi có số xác nhận và số chuyến. Trước đó, nút chất xe
  hỏi ngoại lệ theo thứ tự phát hiện. Xe rời thì đơn Shipped, pallet về Empty, hàng Shipped; họ A gửi ASN.

Foundation chưa có cách gửi tin ra ngoài (bảng cơ chế ghi R11). Lớp Product không có trạng thái Shipped cho MTU.

## Decision

1. **Gửi tin ra ngoài thuộc Foundation, dựng ở R8.** `IPlatform.SendMessage(message, timeout)` gửi đồng bộ và trả
   `MessageReply` (handler tự kiểm, như code thật kiểm trả lời rỗng); `SendMessageAsync` gửi rồi đi tiếp, lỗi chỉ thành dòng
   trace. Tin mang adapter và loại tin; tin đi ngay lúc gọi, ngoài transaction. Đường ra là `IMessageGateway` gắn vào
   `CommandExecutor`: mặc định không có gateway (gửi đồng bộ báo lỗi, như môi trường thiếu client gateway); host cài gateway
   chọn adapter theo tên: adapter file cho nhãn, email, ASN (thư mục outbox), adapter HTTP tới client gateway cho
   `CustomerShipSys` khi môi trường khai `NVM_GATEWAY_URL`. Gateway vào và hệ ngoài giả lập vẫn thuộc R11.
2. **Lớp Product: MTU `Shipped`.** Lệnh `ShipMtu`: Available → Shipped, rời vật chứa, vị trí `n/a`, dòng lịch sử ghi vật chứa
   cũ. Không đếm lại vật chứa.
3. **Đơn bán và chuyến giao trên ShipApp** (`Shipping.cs`, `Loading.cs`, FB `FB_OP_Ship`): `ReceiveSalesOrders` trả Ack
   từng đơn (đơn hỏng không chặn đơn khác trong payload); `PickListGroup` có tên hiển thị riêng, NId là số đếm, nên nhóm đã giao
   đổi tên thêm hậu tố mà không đụng khóa. Chuyến giao (`Delivery`) kiêm lịch chất hàng ở dock. Nơi nhận, loại nhãn, người nhận
   email là master data (`ShipToConfig`).
4. **Nhãn ra file qua adapter máy in.** Lệnh in dựng XML theo mẫu và gửi bất đồng bộ tới adapter `LabelPrinter`; phần mềm in
   đọc file. Mã vạch họ B có bố cục riêng của NovaVolt (khóa 32 ký tự, mặt ở ký tự 33; nhãn hộp 60 ký tự), không chép bố cục
   thật. Skid họ B có NId gồm cả chuyến giao: một đơn khách giao nhiều chuyến thì nhãn skid 001 của chuyến sau trùng mã chuyến
   trước (test bắt được khi ma trận ngoại lệ chạy ca giao một phần).
5. **Ngoại lệ là dữ liệu và tính lại mỗi lần bấm chất xe.** Danh mục mã có nhóm (SkidLevel, SkidBuild, Trailer) và cờ tự khai.
   `CheckTruckLoading` tự khai mã tự động, rồi trả nhóm đầu tiên còn thiếu theo thứ tự SkidLevel → SkidBuild → Trailer cùng các
   mã được chọn; không thiếu thì kiểm xe ở dock. `ConfirmTruckLoad` kiểm lại, gửi SkidBuild (mang ngoại lệ SkidBuild theo
   đơn) rồi ShipLoad (mang ngoại lệ mức kiện và cả chuyến; có mã tự khai thì route mang tiền tố `EX-`).
6. **Xe của khách trên YardApp**: nhận xe vào ô yard gắn với chuyến (route pool phải trùng nơi nhận), Move In, Move Out chỉ ra ô
   yard của nhóm route chính, yard pass `YP-…`, xe rời. Nhóm route là nhóm equipment loại `Route`, thành viên có property
   `LocationType` (`Staging`, `Yard`). ASN do subscriber của event `DeliveryDeparted` gửi sau commit.
7. **Một role `shipping` cho người kho và người ở dock.** Hệ thật tách hai role của app thành phẩm; ai được gán role nào là
   việc của nhà máy, nên NovaVolt giữ một role và ghi lại khác biệt.

## Consequences

**Được**

- Hai họ chạy trọn tới Shipped bằng test; hệ của khách không trả lời (hoặc trả thiếu số, hoặc không có gateway) thì pallet giữ
  Staged, chuyến không Loaded, xe giữ CustomerEmpty; ma trận ngoại lệ sáu ca đúng nhóm và đúng thứ tự.
- Cơ chế gửi tin của nền có test riêng (thiếu gateway, tin đi trước khi command hỏng, adapter theo tên, adapter file).

**Mất / rủi ro**

- Trên stack local không có hệ của khách nên họ B dừng ở Confirm như trên máy ảo, trừ khi trỏ `NVM_GATEWAY_URL` tới một hệ giả
  (R11).
- Email đi ra file outbox, chưa qua SMTP (Mailpit) tới R11.
- Ngoại lệ "giao bổ sung khi chuyến gồm nhiều nhóm đơn" chưa dựng: ở NovaVolt một chuyến giao là một nhóm.

## Alternatives

| Phương án | Vì sao không chọn |
|---|---|
| Để gửi tin tới R11, R8 gọi thẳng HTTP trong handler | Trái K9 và mất chỗ chẩn đoán chung (trace của platform, gateway mặc định báo lỗi như môi trường thật) |
| Gửi tin trong outbox, đi sau commit | Hệ thật gửi đồng bộ ngay trong handler để lấy số xác nhận trước khi chất; outbox làm mất hành vi "không có số thì không chất" |
| Ngoại lệ kiểm một lần, lưu cờ "đã kiểm" trên chuyến | Lượng staged đổi sau mỗi lần quét; tính lại mỗi lần bấm khớp hành vi màn và không có cờ cũ |
| Đổi NId của nhóm đã giao để nhường tên | Đơn, chuyến tham chiếu nhóm theo NId; đổi khóa phải sửa dây chuyền tham chiếu |
