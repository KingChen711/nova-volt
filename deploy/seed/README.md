# Seed

Dữ liệu master data nạp lúc khởi động, chưa nằm trong database.

## `factory-model.r<n>.json` — cây ISA-95, **một file cho mỗi revision**

Nguồn sự thật của cây `Enterprise → Site → Area → Line → WorkCell → Equipment` cho `NV1` và `DE1`.

Thư mục này là **cả kệ tài liệu**, không phải một bản duy nhất. Mỗi file là một revision đã publish,
và cả ba được nạp lúc khởi động (`IFactoryModelCatalog`, `ADR-024`). Cái nào **đang có hiệu lực** ở
plant nào là chuyện khác, do lệnh activate quyết định.

| File | Publish | Đổi gì |
|---|---|---|
| `factory-model.r1.json` | 2026-08-26 | Bản đầu. 41 node |
| `factory-model.r2.json` | 2026-09-02 | `NV1`: `FORM-01` nâng từ **4 lên 8 kênh sạc**. `DE1` không đổi |
| `factory-model.r3.json` | 2026-09-16 | `NV1`: **tháo `FORM-02`** (đại tu dài hạn), **thêm `STACK-04`** vào `L2`. `DE1`: thêm `EOL-01` |

> **Đây không phải bản rút gọn của "cái thật".** Trong nhà máy thật, cây nhà máy hiếm khi được gõ
> tay vào MES — nó đến từ **bản vẽ kỹ thuật và hệ thống engineering** dưới dạng một file import. File
> này là **bước đầu tiên của đúng đường import đó**; M11 (*ERP B2MML & Master Data Reconciliation*)
> thay nguồn, không thay hình dạng. M5 cho nó một chỗ ở trong SQL Server.
> Xem `docs/plans/M1-factory-model-bus.md` §3.1.

### Quy tắc

1. **Đổi cây = thêm một file mới, KHÔNG sửa file cũ.** Một revision đã publish là hồ sơ: hồ sơ
   traceability năm ngoái trỏ tới một `equipment_path` có thể đã bị tháo, và chỉ tài liệu có hiệu lực
   *lúc đó* trả lời được *"thiết bị này thuộc line nào"* (`docs/plans/M1-factory-model-bus.md` §C07.1,
   `ADR-024`). Sửa đè lên một file đã publish là xoá mất câu trả lời.
2. **Tên file và `revision` bên trong phải khớp.** Copy `r2` thành `r3` rồi quên sửa số bên trong sẽ
   đưa **cây cũ** vào hiệu lực dưới một số revision mới — một thay đổi mà ai cũng tin là đã xảy ra và
   không xảy ra. Khởi động sẽ chết với thông báo nêu đúng file sai.
3. **File có tên đúng dạng nhưng số không đọc được thì chặn khởi động**, không bị bỏ qua. Một tài
   liệu bị lờ đi im lặng là một đợt rollout biến mất, và chỉ lộ ra vào ca phải kích hoạt nó.
4. **Số revision được phép có khoảng trống.** Kệ giữ 1, 2 và 5 nghĩa là 3 và 4 được soạn rồi không
   publish. Đó là chuyện bình thường, không phải dữ liệu hỏng.
5. **Mã viết HOA.** Chữ thường bị parser từ chối, không được tự hạ hoa: một máy hai node là một máy
   có lịch sử bị chia đôi.
6. **Độ sâu quyết định bậc.** Con của Site luôn là Area, dù đặt tên nó là gì. Không có bậc thứ 7 —
   cảm biến bên trong một kênh sạc là **thuộc tính của equipment**.
7. **`timeZoneId` chỉ có ở bậc Site**, và bắt buộc ở đó. `DE1` cố ý dùng `Europe/Berlin` vì nó
   **có DST** — `IProductionCalendar` ở M3 phải có test cho cả hai ngày chuyển giờ.
8. **Sửa `r1` sẽ làm test đếm node đỏ.** `FactoryModelSeedTests` đếm cứng từng bậc của revision 1. Đó
   là hành vi **đúng** và là hàng rào của quy tắc 1: một revision đã publish không được đổi bằng tay.
   Cần một cây khác thì thêm `r4`, đừng sửa `r1`.

### Quy mô

Đây là quy mô **lab**, không phải quy mô sản xuất. Cụ thể: `FORM-01` có **4** kênh sạc ở `r1` và
**8** từ `r2`, máy thật có khoảng **1.000** (`docs/scope.md` §2.4). Seed đủ lớn để mọi bậc của cây có
mặt thật, đủ nhỏ để đọc được bằng mắt — và ba revision đủ để một lần chuyển cây có cả phần **thêm**
lẫn phần **bớt**, thứ mà một revision duy nhất không bao giờ tạo ra được.
