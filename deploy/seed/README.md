# Seed

Dữ liệu master data nạp lúc khởi động, chưa nằm trong database.

## `factory-model.json` — cây ISA-95

Nguồn sự thật của cây `Enterprise → Site → Area → Line → WorkCell → Equipment` cho `NV1` và `DE1`.

> **Đây không phải bản rút gọn của "cái thật".** Trong nhà máy thật, cây nhà máy hiếm khi được gõ
> tay vào MES — nó đến từ **bản vẽ kỹ thuật và hệ thống engineering** dưới dạng một file import. File
> này là **bước đầu tiên của đúng đường import đó**; M11 (*ERP B2MML & Master Data Reconciliation*)
> thay nguồn, không thay hình dạng. M5 cho nó một chỗ ở trong SQL Server.
> Xem `docs/plans/M1-factory-model-bus.md` §3.1.

### Quy tắc

1. **`revision` tăng mỗi lần đổi.** Không sửa tại chỗ rồi giữ nguyên số. Hồ sơ traceability năm
   ngoái trỏ tới `equipment_path` có thể đã bị tháo — mất `revision` là mất khả năng trả lời
   *"lúc đó thiết bị này thuộc line nào"* (`docs/plans/M1-factory-model-bus.md` §C07.1).
2. **Mã viết HOA.** Chữ thường bị parser từ chối, không được tự hạ hoa: một máy hai node là một máy
   có lịch sử bị chia đôi.
3. **Độ sâu quyết định bậc.** Con của Site luôn là Area, dù đặt tên nó là gì. Không có bậc thứ 7 —
   cảm biến bên trong một kênh sạc là **thuộc tính của equipment**.
4. **`timeZoneId` chỉ có ở bậc Site**, và bắt buộc ở đó. `DE1` cố ý dùng `Europe/Berlin` vì nó
   **có DST** — `IProductionCalendar` ở M3 phải có test cho cả hai ngày chuyển giờ.
5. **Sửa file này sẽ làm test đếm node đỏ.** Đó là hành vi **đúng**, không phải phiền toái. Cập nhật
   con số trong test cùng lúc, và coi đó là một lần xác nhận có chủ đích.

### Quy mô

Đây là quy mô **lab**, không phải quy mô sản xuất. Cụ thể: `FORM-01` ở đây có **4** kênh sạc, máy
thật có khoảng **1.000** (`docs/scope.md` §2.4). Seed đủ lớn để mọi bậc của cây có mặt thật, đủ nhỏ
để đọc được bằng mắt.
