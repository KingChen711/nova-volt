# Thiết kế giao diện web

Chốt ở R2 bằng skill `ui-ux-pro-max` (truy vấn "manufacturing operations internal tool dashboard industrial
data-dense", density 8, variance 3, motion 2) rồi chỉnh theo bối cảnh: đây là công cụ nội bộ, không phải trang giới
thiệu, nên bỏ pattern landing và style "Exaggerated Minimalism" mà bộ sinh gợi ý, lấy style **Data-Dense Dashboard**.

## Token (`app/globals.css`)

**Không dùng nền xanh đen** (chủ repo, 2026-10-28): nền trang, mặt panel, sidebar và nền dark mode không được là xanh
đậm hay đen ánh xanh. Nền tối dùng xám trung tính (`zinc`/`neutral`/`stone`); xanh chỉ làm màu nhấn (nút, vùng
chọn, link). Ngày 2026-10-28 đổi toàn bộ nhóm trung tính từ slate (ánh xanh) sang zinc.

| Nhóm | Light | Dark | Dùng cho |
|---|---|---|---|
| Nền / mặt / mặt nổi | `#F4F4F5` / `#FFFFFF` / `#FAFAFA` | `#111113` / `#19191C` / `#222226` | Trang, panel, header bảng |
| Đường kẻ | `#E4E4E7` | `#323238` | Viền panel, kẻ bảng |
| Chữ / chữ phụ | `#18181B` / `#52525B` | `#E4E4E7` / `#A1A1AA` | |
| Sidebar / mục nổi | `#1C1C1F` / `#2E2E33` | `#0B0B0D` / `#232327` | Xám than, chữ `#E4E4E7`, vạch hổ phách |
| Chính | `#1E40AF` | `#60A5FA` | Nút chính, khu đang chọn |
| Chính nhạt | `#DBEAFE` | `#27272A` (xám, không dùng xanh đen) | Ô biểu tượng, badge, hover hàng |
| Nhấn | `#B45309` | `#F59E0B` | Điểm cần chú ý |
| Trạng thái | đạt `#15803D`, cảnh báo `#B45309`, lỗi `#B91C1C` | sáng hơn tương ứng | Luôn kèm chữ, không chỉ màu |

Chữ: **Fira Sans** cho nội dung, **Fira Code** cho mã, Id, số (tách bạch `0/O`, `1/l`). Fira Code không có dấu tiếng
Việt: không dùng chữ mono cho câu tiếng Việt.

## Bố cục

- Sidebar 256 px **xám than ở cả hai chế độ** (`--color-nav`), tách vùng điều hướng khỏi vùng dữ liệu: logo, plant,
  khu nhóm theo mảng (Tổng quan, Vận hành, Chất lượng, Hậu cần, Quản trị), vạch hổ phách đánh dấu khu đang mở, người
  dùng và role ở đáy. Dưới 1024 px sidebar thành ngăn trượt có lớp phủ.
- Header 64 px: breadcrumb (mảng › khu), chấm trạng thái kết nối tới host MOM (hỏi `/healthz` mỗi 30 giây), giờ.
- Trang Tổng quan: thẻ số liệu đọc bằng `$count` của OData, lưới khu theo role có badge mốc, bảng timer sắp tới hạn.
- Khu chưa dựng: không để trống, liệt kê các màn dự kiến, mốc làm, role được vào.
- Panel: bo 12 px, bóng nhẹ, thanh tiêu đề có số dòng và nút tải lại. Bảng: hàng 40 px, header chữ hoa nhỏ, skeleton
  khi tải, trạng thái trống có biểu tượng và câu giải thích.
- Trang đăng nhập hai nửa: mảng thương hiệu nền lưới bản vẽ (`.blueprint`) và form.
- Vùng bấm ≥ 44 px (tablet ở xưởng, có thể đeo găng), khoảng cách giữa nút ≥ 8 px.

## Luật tương tác

- Menu và route theo realm role trong token; `mom-admin` thấy mọi khu. Ẩn menu không phải phân quyền: service layer
  vẫn phải kiểm.
- Form: label luôn hiện, dấu `*` cho trường bắt buộc, gợi ý dưới ô. Nút hiện trạng thái đang gửi.
- Kết quả command hiện nguyên thân response (mã HTTP, `CorrelationId`, JSON), `role="alert"` khi lỗi. Không dịch,
  không rút gọn lỗi: người đọc cần `ErrorCode` và câu thật để chẩn đoán (lab L13). Thành công thì thêm toast ngắn ở
  góc và bảng tự tải lại.
- Chuyển động 150–200 ms, tắt khi `prefers-reduced-motion`. Viền focus luôn thấy.
