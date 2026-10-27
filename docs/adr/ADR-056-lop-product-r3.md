# ADR-056 — Lớp Product: ba FB theo domain, một base app `Production`, trạng thái MTU cố định, trạng thái WO là dữ liệu

| | |
|---|---|
| **Status** | **Accepted** — agent quyết theo AGENTS.md §2.3, chờ chủ repo đọc |
| **Date** | 2026-10-28 |
| **Liên quan** | ADR-054 · ADR-055 · scope §3 · [bảng cơ chế](../oef-mapping.md#lớp-product) |

## Context

R3 dựng lớp đóng vai sản phẩm của nhà cung cấp: Equipment, Material, BoP/Process/Operation, MTU và vật chứa, WO,
NC, containment, buffer, yêu cầu vận chuyển. Từ R4, phía dự án mở rộng lớp này bằng facet, pre-check, post-action và
extension app mà không sửa nó, nên ranh giới "lớp Product làm gì, phía dự án làm gì" phải theo đúng hệ thật đã quan
sát:

- Lệnh ghi sản phẩm vào WO không tự cộng số lượng; số chỉ đổi khi truyền phần cộng thêm.
- Gắn một MTU thẳng vào pallet đang chứa tote thì lớp nhà cung cấp chỉ gắn; tháo tote là post-action của team.
- NC OPEN giữ MTU; USE AS IS thả MTU chỉ khi không còn NC OPEN khác; hủy hàng loạt tạo NC mới ở SCRAP và để nguyên
  NC OPEN cũ.
- Containment Close không nhả hàng; CR không có MTU nằm ở Editing.
- Bộ trạng thái WO gồm cả trạng thái team thêm (Frozen) và bảng role theo transition; hủy WO Frozen bị chặn theo role.
- Mã scrap, scrap tag, cost center, tên NC tự sinh, đổi tên CR theo mã trong tên là phần của team.

## Decision

1. **Ba FB theo domain**: `FB_MS_Product` (equipment, material, process, buffer), `FB_OP_Product` (MTU, MTUA, lịch sử
   MTU, WO, NC, containment, gán buffer, yêu cầu vận chuyển), `FB_RF_Product` (defect, cài bộ trạng thái WO). Handler
   OP đọc master data bằng `ProjectionQuery`, nên chỉ thấy master data đã commit (NVM004 chặn `Query` khác domain).
2. **Một base app `Production`** import toàn bộ 27 entity và 43 command. Extension app của dự án (R4) gắn vào nó.
3. **Trạng thái MTU là hằng số** (Available, OnHold, Scrapped, Repair) do NC và containment đổi trực tiếp. **Trạng thái
   WO là state machine dạng dữ liệu** `WorkOrder`; lớp Product chỉ cung cấp lệnh `InstallWorkOrderStates` nạp bộ mặc
   định (Edit, New, Active, Pause, Complete, Aborted). `SetWorkOrderStatusForced` bỏ qua transition và role nhưng vẫn
   đòi trạng thái đích có trong dữ liệu. Mọi lần đổi trạng thái WO phát `WorkOrderStatusChanged` sau commit.
4. **Khóa logic ghép cho bảng liên kết** (`<WO>:<Op>`, `<NC>|<MTU>`…); gán buffer lấy NId của MTU làm NId, nên unique
   index `(IsDeleted, NId)` của nền ép "mỗi lô một buffer".
5. **Lịch sử MTU chỉ thêm dòng** (K4): `Create`, `Assemble`, `Edit`. Assemble của sản phẩm mang MTU nguyên liệu và
   lượng trừ: đó là genealogy. Vị trí trong vật chứa tra bằng `MtuLocations.EquipmentOf` (đi ngược tới tầng ngoài).
6. **Không có màn web ở R3**: theo plan, màn đi cùng luồng từ R4.

Không thuộc quyết định này: tên NC tự sinh, scrap tag, mã scrap/problem/model, cost center, cấu hình hủy theo
operation, đổi tên CR theo mã, WO theo ca. Tất cả là phần của team (R4–R6).

## Consequences

**Được**

- Mỗi command công bố có test hành vi; test `Every_published_command_is_exercised_by_a_test` hỏng khi thêm command mà
  quên test.
- Phía dự án thêm Frozen và role cho Frozen → Aborted chỉ bằng dữ liệu (đã có test).
- Ranh giới nhà cung cấp/team rõ, nên lab R6–R7 (post-action tháo tote, NC tự sinh) có chỗ để cắm.

**Mất / phải chịu**

- Handler OP không thấy master data chưa commit: tạo material rồi tạo MTU trong cùng một chuỗi `CallCommand` sẽ báo
  material không tồn tại. Đúng theo luật domain, nhưng suite master data (R4) phải nạp master data thành lời gọi riêng.
- Vài hành vi chưa có nguồn quan sát được dựng theo cách đơn giản nhất và ghi ở bảng giả định (bảng cơ chế): kiểm
  lô thiếu khi trừ nguyên liệu, thả danh sách MTU, cấp số CR, yêu cầu vận chuyển. R6–R10 đọc nguồn kỹ hơn có thể phải
  sửa.
- Trạng thái MTU cố định nghĩa là phía dự án không thêm trạng thái MTU bằng dữ liệu được. Hệ thật có làm vậy không thì
  chưa thấy.
- Tra vị trí trong vật chứa chạy nhiều câu truy vấn (một câu mỗi tầng), không phải một câu đệ quy.

**Việc phát sinh**

- R4: suite master data gọi `InstallWorkOrderStates`, rồi thêm Frozen và role bằng dữ liệu; extension app
  `NvProductionExt` gắn vào `Production`.
- R6: NC tự sinh, scrap tag, mã hủy là facet và post-action của team trên `NonConformance`.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Trạng thái MTU cũng là state machine dạng dữ liệu | Nguồn chỉ thấy team thêm trạng thái WO (Frozen), không thấy thêm trạng thái MTU; thêm một lớp dữ liệu không có hành vi để kiểm |
| Mỗi mảng một base app (Equipment, Material, Production…) như sản phẩm thật | Plan chọn một base app `Production` cho phần các luồng của team dùng tới; tách app thêm việc cấu hình mà R4–R13 không dùng |
| Material và revision chung một entity | Nền chỉ có một khóa logic `NId`; tách `Material` / `MaterialRevision` giữ được "một revision hiện hành" mà không đổi khóa |
| Lớp Product tự sinh tên NC, scrap tag | Đó là code của team ở hệ thật; làm ở lớp Product thì R6 không còn gì để mở rộng |

## Evidence

- `dotnet test --project tests/Integration/Nvm.ProductTests`: 30/30 xanh, SQL Server thật (Testcontainers).
- Nguồn hành vi: bài `material-mtu-vat-chua`, `ban-thiet-ke-va-ca`, `hang-loi`, `containment-request`,
  `load-register` của khóa MOM (đọc 2026-10-28). Phần dựng theo phán đoán ghi ở bảng giả định của bảng cơ chế.
