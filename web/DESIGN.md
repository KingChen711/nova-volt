# Thiết kế giao diện web

AWS Console dark theme (chủ repo, 2026-10-30): hệ thiết kế Cloudscape bản visual refresh, một theme tối duy nhất, không có theme sáng.
Giá trị lấy từ gói `@cloudscape-design/design-tokens` 3.0.117 và đo trên trang demo `cloudscape.design/examples/react/table.html` ở dark
mode. Chủ repo chấp nhận nền xanh đen của Cloudscape, thay luật "không nền xanh đen" ngày 2026-10-28. Chỉ mượn phong cách; web tự viết bằng
Tailwind, không dùng thư viện component của Cloudscape, không dùng logo hay tên AWS. Không dùng skill thiết kế nào.

## Token (`app/globals.css`)

| Nhóm | Giá trị | Dùng cho |
|---|---|---|
| Nền, thanh trên, menu, khung | `#161D26` | Mọi mặt chính |
| Đầu bảng | `#131920` | |
| Hover, badge xám | `#1B232D`, `#232B37` | |
| Đường kẻ, kẻ giữa hàng | `#424650`, `#232B37` | Viền khung, kẻ đầu bảng; kẻ giữa hàng |
| Viền ô nhập, chữ nút tắt | `#656871`, `#8C8C94` | |
| Chữ, tiêu đề, đầu cột | `#C6C6CD`, `#EBEBF0`, `#B4B4BB` | |
| Chính, hover | `#42B4FF`, `#75CFFF`; chữ trên nút chính `#0F141A` | Nút, link, khu đang chọn, focus |
| Đạt, cảnh báo, lỗi | `#2BB534`, `#FBD332`, `#FF7A7A`; nền alert `#001401`, `#191100`, `#1F0000` | Trạng thái |
| Flashbar thành công | `#00802F`, chữ `#F9F9FA` | |

Chữ: **Open Sans** (có tiếng Việt) 14/20 px; tiêu đề trang 24 px đậm, tiêu đề khung 18 px đậm, chữ phụ 12 px. Mã, Id, số dùng
monospace hệ thống; không dùng mono cho câu tiếng Việt.

## Bố cục

- Thanh trên 56 px, kẻ dưới: tên, plant, trạng thái kết nối host MOM (hỏi `/healthz` mỗi 30 giây), giờ, người dùng và role, đăng xuất.
- Side navigation 280 px, nhóm theo mảng (Vận hành, Chất lượng, Hậu cần, Quản trị), không icon; khu đang mở chữ đậm xanh.
  Dưới 1024 px thành ngăn trượt.
- Breadcrumb 40 px trên nội dung.
- Đầu trang: tiêu đề, mô tả xám, hành động bên phải. Nhiều màn trong một khu: tab gạch chân.
- Khung: viền 1 px, bo 16 px, không bóng. Tiêu đề kèm số đếm `Tên (n)`.
- Bảng: đầu cột chữ đậm, hàng 40 px, mã không xuống dòng, bảng rộng cuộn trong khung.
- Trang đăng nhập: thanh trên và một khung giữa trang.

## Thành phần (`components/ui.tsx`)

- Nút: bo 20 px, viền 2 px, chữ đậm, cao 32 px. `primary` nền xanh đặc; `secondary` viền và chữ xanh; `ghost` chữ xanh không viền;
  `danger` chữ đỏ không viền; nút tắt viền và chữ xám.
- Trạng thái: icon và chữ cùng màu, không có nền (`Badge` tone `ok`, `warn`, `bad`, `primary`); `neutral` là badge xám bo 4 px.
- Ô nhập: cao 32 px, bo 8 px, nhãn đậm phía trên, gợi ý 12 px phía dưới.
- Kết quả command (`ResponsePanel`): alert viền 2 px, nền nhạt theo trạng thái, hiện nguyên thân response.
- Thành công (`Toast`): flashbar xanh lá đặc, bo 12 px, ngang vùng nội dung.

## Luật tương tác

- Menu và route theo realm role trong token; `mom-admin` thấy mọi khu. Ẩn menu không phải phân quyền: service layer vẫn phải kiểm.
- Trạng thái luôn kèm chữ. Không dịch, không rút gọn lỗi: người đọc cần `ErrorCode` và câu thật để chẩn đoán (lab L13).
- Chuyển động chỉ đổi màu 150 ms, tắt khi `prefers-reduced-motion`. Viền focus luôn thấy.
- Vùng bấm 32 px theo console, không theo chuẩn 44 px: người dùng chính thao tác bằng chuột trên máy cá nhân.
