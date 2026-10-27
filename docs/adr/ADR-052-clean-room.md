# ADR-052 — Clean-room: repo public không chứa tên, mã, code của dự án thật

| | |
|---|---|
| **Status** | **Accepted** — chủ repo duyệt 2026-10-27 |
| **Date** | 2026-10-27 |
| **Liên quan** | ADR-051 · AGENTS.md K14 · `tools/cleanroom/` |

## Context

v2 mô phỏng một hệ MOM thật của một khách hàng (ADR-051). Repo này và site tài liệu đi kèm đều
**public** trên GitHub. Tài liệu tìm hiểu hệ thật chứa tên khách hàng, nhà cung cấp, hệ thống nội bộ,
part number, mã code, bố cục nhãn, câu báo lỗi và code của team; không thứ nào được phép lên repo
công khai. Một khi đã push, nội dung nằm trong lịch sử git và không rút lại được.

## Decision

- Viết lại từ mô tả hành vi; không chép code, SQL, suite, CSV, file khai báo model, câu báo lỗi
  nguyên văn của hệ thật.
- Không dùng tên, mã, tiền tố, định dạng thật. **Giữ cấu trúc, đổi bề mặt**: giữ loại quy tắc
  (barcode độ dài cố định, chính sách gửi lại theo cặp trạng thái và mã lỗi…), đổi giá trị.
- Tên lệnh, entity của nền tảng nhà cung cấp cũng đặt lại.
- Bảng đối chiếu tên thật và danh sách từ cấm nằm ngoài git (thư mục riêng, bị `.git/info/exclude`).
- Kiểm tự động: `tools/cleanroom/check.py` quét file git theo dõi. Repo chỉ chứa **HMAC-SHA256** của
  từng từ cấm (`tools/cleanroom/denylist.hmac`); khóa là secret (`NVM_CLEANROOM_KEY` trên GitHub,
  file riêng trên máy cá nhân). Chạy ở pre-commit hook, `make ci` và job CI `Clean-room`.

## Consequences

**Được**

- Lỗi lộ tên bị chặn trước commit và trên CI, không phụ thuộc trí nhớ.
- Danh sách từ cấm không tự lộ: băm thường thì ai cũng thử băm được từ ngắn, HMAC có khóa thì không.

**Mất / phải chịu**

- Mỗi lần đọc tài liệu tìm hiểu phải dịch sang tên giả; dễ sai lệch nghĩa nếu bảng đối chiếu không rõ.
- PR từ fork không có secret nên job chỉ bắt buộc khi push lên `main`.
- Phép quét theo token: một từ cấm bị viết tách bằng ký tự khác hoặc diễn đạt bằng lời vẫn lọt.
  Đây là lưới an toàn, không thay việc tự kiểm.
- Từ cấm ngắn có thể khớp nhầm; file khóa phụ thuộc (`package-lock.json`…) được bỏ qua vì lý do này.

**Việc phát sinh**

- Bảng đối chiếu tên chốt ở R0, cập nhật khi gặp khái niệm mới.
- Site tài liệu dùng cùng phép kiểm khi viết lại.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Chuyển repo sang private | Dữ liệu khách hàng vẫn không nên nằm trên GitHub cá nhân; repo private còn bị tính phút Actions |
| Danh sách từ cấm dạng chữ trong repo | Chính danh sách là thông tin cần giấu |
| Băm SHA-256 không khóa | Từ cấm ngắn, dễ đoán: băm thử từ điển là ra |

## Evidence

Trên commit R0: `python tools/cleanroom/check.py` báo `ok` trên toàn repo; một file thử chứa tên bị
cấm (ghép camelCase) bị chặn ở chế độ `--staged` với vị trí `file:dòng`, không in từ khớp.
