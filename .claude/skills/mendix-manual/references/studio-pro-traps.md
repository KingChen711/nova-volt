# Bẫy Studio Pro đã được kiểm chứng

Nguồn: ledger của dự án CGVibe, Studio Pro 11.12.1. Đây là những thứ **đã tái lập được**,
không phải phỏng đoán. Sắp theo thời điểm chúng cắn.

> [!warning] Điểm chung của gần hết danh sách này
> Chúng **không** hiện lên ở Error List, **không** hiện ở `mx check`, và chỉ lộ ra ở F5
> hoặc ở runtime thật. Một model "0 errors" không có nghĩa là app chạy.

---

## 1. Khi kiểm tra lỗi

### F4 không kiểm tra gì cả

`F4` = **Synchronize App Directory**. Nó nạp lại file trên đĩa (widget, theme, Java action)
vào model. Nó chạy vài giây rồi im lặng, và người ta tưởng "đã check, không lỗi".

Kiểm nhất quán thật: **View → Error List → Check now**. Chạy app: **F5**.

### `mx check` nói dối khi có tab chưa lưu

`mx check` đọc file `.mpr` **trên đĩa**. Studio Pro giữ tài liệu đang sửa **trong bộ nhớ**
cho tới khi lưu, và `Ctrl+S` chỉ lưu **tab đang active**.

Hậu quả: một tab chưa lưu làm `mx check` báo hàng loạt `CE1613` — *"… no longer exists"* —
về những phần tử **đang tồn tại và hoàn toàn bình thường**. Ở CGVibe chuyện này đã hai lần
khiến người ta đi sửa một mapping vốn không hỏng.

Trước khi chạy `mx check`:
1. **File → Save All**
2. Nhìn tab bar — **chấm tròn trên tab nghĩa là chưa lưu**
3. Chỉ khi không còn chấm nào mới chạy

Khi `mx check` và Error List mâu thuẫn: **tin Error List**, vì nó mô tả model đang sống.

### Exit code của `mx check` là bitmask

`0` = sạch. `1` lỗi · `2` cảnh báo · `4` deprecation · `8` hiệu năng. Cộng dồn. Đừng báo
"exit code khác 0 nên fail" — phải tách bit ra.

### `CW0114` có dương tính giả

Cảnh báo *"accessible through the server API … not used from navigation, a page or a
published service"* **không** đủ để kết luận một microflow đã chết. Ở CGVibe nó báo nhầm cho
ba microflow đang phục vụ traffic thật. Đối chiếu với published service trước khi xoá.

---

## 2. Khi tạo page và microflow mới

### Danh sách allowed roles rỗng — bẫy tốn thời gian nhất

Một page mới có thể ra đời với **allowed roles rỗng**. Rỗng **không phải lỗi model**, nên nó:

- qua được Error List ✓
- qua được `mx check` ✓
- rồi **không role nào mở được page**, chỉ lộ ở F5

Điều tương tự với **microflow dùng làm home page theo role**: allowed roles rỗng thì mọi
kiểm tra đều xanh, rồi hỏng đúng ở role mà nó sinh ra để phục vụ.

**Luôn mở properties của page/microflow mới và nhìn tận mắt danh sách roles.** So với một
page tương đương đã chạy được.

### Entity access biến `setValue` thành no-op im lặng

Một widget ghi vào attribute mà module role chỉ có quyền **Read** sẽ ghi hụt, **không báo lỗi**.
Cùng triệu chứng với DataView để `editability: Never`.

Khi một giá trị "không chịu lưu": kiểm access rule của attribute trước khi nghi ngờ widget.

---

## 3. Khi gọi backend

### Error handling của Call REST nằm ở chuột phải, không phải trong dialog

Double-click activity **Call REST service** → tab **Error handling** chỉ có checkbox
`$latestHttpResponse`. Người ta kết luận "không có tuỳ chọn error handling" và bỏ qua.

Chỗ đúng: **chuột phải lên activity → Set error handling → Custom without rollback**.

Vì sao quan trọng: mặc định là **Rollback**. Với BFF chuyển tiếp, một `400`/`401`/`403` từ
upstream sẽ **rollback cả microflow** và client nhận trang lỗi Mendix thay vì envelope theo
hợp đồng.

### `Custom without rollback` cần một nhánh error đi ra

Sau khi đổi sang Custom without rollback, activity **bắt buộc** có flow error đi ra. Nếu
chưa có, Error List báo hai lỗi *"Sequence flow is not accepted by origin or destination"*.

**Xoá cái flow "bị từ chối" đó sẽ làm Error List sạch và phá huỷ hợp đồng lỗi** — mọi 4xx
upstream biến thành 503 tự chế. Ở CGVibe đây là một defect thật.

Cách đúng: nối nhánh error tới một End event trả `$latestHttpResponse`.

### Mendix từ chối bằng 404, không phải 403

Khi role không được phép gọi một published operation, Mendix trả **404 không có body**, để
không tiết lộ rằng tài nguyên tồn tại. Đừng nhầm với "backend nói không tìm thấy".

Phân biệt bằng **hình dạng envelope**: Mendix trả `{"error":{"code":"404",...}}`, còn backend
của dự án trả envelope riêng có `reasonCode`.

### 401 hết phiên của Mendix khác envelope của backend

Mendix sinh `{"error":{"code":"401","message":"…"}}` **trước khi** microflow chạy. Client nào
phân nhánh theo `reasonCode` trong envelope sẽ bỏ sót nó. **Rẽ theo HTTP status trước.**

---

## 4. Khi làm security

### Published REST liệt kê module role, nhưng runtime phân quyền theo user role

Trường **Allowed roles** của một published REST service hiện **module role**. Runtime lại
phân quyền theo **user role**, và Studio Pro giải quyết ánh xạ đó lúc build.

Hệ quả: một module role mà **không user role nào bao gồm** thì **không có tác dụng gì**,
trong khi editor vẫn hiển thị nó như đã cấu hình. Error List, `mx check`, OpenAPI sinh ra, và
cả probe `401`/`404` — tất cả đều hành xử y như một service đang hoạt động.

Cách kiểm thật: sau khi deploy, đọc `AllowedUserRoles` trong `deployment/model/model.mdp`.
Hoặc đơn giản hơn: **gọi thử bằng session của role mới**. Bảng role không phải bằng chứng.

### Allowed roles là thuộc tính của service, không phải của từng operation

Mở một operation cho role mới nghĩa là mở **toàn bộ service** đó ở ranh giới Mendix. Cổng
thứ hai là allowed roles của chính response microflow.

---

## 5. Khi làm version control

### `Merge Changes Here` merge VÀO branch đang mở

Mở branch **đích** trước, rồi **Version Control → Merge Changes Here**, chọn branch nguồn.
Không đi qua **Manage Branch Lines**.

### Không bao giờ "Revert All Changes"

Đó là thao tác mất dữ liệu. Khi Studio Pro chặn vì working copy dirty:

1. Dừng lại, đừng revert
2. Xem **Changes in Model** và **Changes on Disk** riêng biệt
3. Quyết định thay đổi đó thuộc về việc nào
4. Nếu không rõ chủ sở hữu: **để nguyên copy đó**, tải một copy khác về thư mục mới và làm ở đó

### Merge có thể xoá âm thầm một activity trong microflow

Đã xảy ra ở CGVibe: sau merge, một activity biến mất, gây một chùm `CE0108`/`CE0109` gọi tên
các biến — và lỗi lại hiện ở document của story khác, nên rất khó lần.

Sau mỗi merge có đụng microflow: chạy Check now **và** so lại số activity của những microflow
bị chạm.

### "Feature branch sạch" không có nghĩa là "đã merge"

Ba câu hỏi khác nhau, phải trả lời riêng: **đã commit chưa · đã push chưa · branch tích hợp
đã chứa nó chưa**.

---

## 6. Khi dùng widget và thư viện

### `.mpk` mới copy vào là vô hình cho tới khi F4

Chép file `.mpk` vào `widgets/` **không** đăng ký nó với model đang mở. Nhấn **F4**
(Synchronize App Directory) thì nó mới xuất hiện.

### Nút Sign-out trong Toolbox có thể không phải logout của bạn

Nó thực hiện sign-out của nền tảng **trực tiếp**, nên microflow cầu nối để thu hồi session ở
backend **không bao giờ chạy**. Ở CGVibe, refresh token phía Identity vẫn sống đủ 7 ngày sau
một lần "đăng xuất thành công".

Dùng Button thường trỏ tới microflow logout của mình.

### Điều khiển ở phạm vi session thuộc về layout, không thuộc page

Nút logout đặt trên một page chỉ phủ đúng page đó. Mọi page thêm sau này sẽ không có chỗ
đăng xuất. Đặt ở **layout** dùng chung.

---

## 7. Bảng triệu chứng → nghi phạm đầu tiên

| Triệu chứng | Nghi phạm | Kiểm gì trước |
|---|---|---|
| Nhiều app trùng tên trong launcher | Nhiều thư mục local khác nhau | Liệt kê đường dẫn `.mpr` tuyệt đối |
| `mx check` báo `CE1613` hàng loạt | Tab chưa lưu | Nhìn chấm trên tab → Save All → chạy lại |
| Không chuyển/merge branch được | Working copy dirty thật | Xem cả hai tab Changes, xác định chủ sở hữu |
| Mọi check xanh nhưng page 404 ở F5 | Allowed roles rỗng | Mở properties của page, nhìn danh sách roles |
| Giá trị không chịu lưu | Entity access chỉ Read | Kiểm access rule của attribute |
| Mọi lỗi upstream thành 503 | Call REST đang ở Rollback | Chuột phải activity → Set error handling |
| Gọi API trả 404 rỗng | Mendix từ chối theo role | So hình dạng envelope; gọi lại bằng role được phép |
| Widget mới không xuất hiện | `.mpk` chưa đồng bộ | F4 |

---

## Nguồn chính thống

- https://docs.mendix.com/refguide/consistency-errors/
- https://docs.mendix.com/refguide/mx-command-line-tool/app/
- https://docs.mendix.com/refguide/pushing-pulling/
- https://docs.mendix.com/refguide/resolving-conflicts/
- https://docs.mendix.com/refguide/troubleshoot-version-control-issues/
