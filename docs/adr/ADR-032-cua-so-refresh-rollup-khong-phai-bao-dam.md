# ADR-032 — Cửa sổ refresh 5 giờ không phải bảo đảm; cần ba lớp chống dữ liệu đến muộn

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-30 |
| **Liên quan** | `ADR-011`, `ADR-028`, `ADR-029`, `docs/plans/M3-telemetry-timescaledb-production-calendar.md` §2.2 và §3.3, D5 |

---

## Context

Continuous aggregate 1 phút giúp đọc đường cong dài ngày mà không quét lại từng điểm thô. Nó chỉ
đúng khi bucket đã materialize chứa đủ những row thuộc về bucket đó. Gateway xả backlog và clock
thiết bị lệch đều làm row tới database sau thời điểm vật lý mà row mang theo.

Ba con số thường bị trộn thành một nhưng có nghĩa khác nhau:

- `5 phút` là ngưỡng `ClockQualityClassifier` đổi nhãn từ `Good` sang `Drifted`; ingestion vẫn giữ
  row, nên nó không phải trần độ trễ;
- `2 giờ` là backlog được mô hình trong scope, và simulator cũng có fault clock skew `2 giờ`;
- `2 GiB` là trần thật của persistent gateway buffer. Code không có trần tuổi, nên không tồn tại
  một upper bound theo giờ có thể suy ra chỉ từ cấu hình hiện tại.

Vì vậy `start_offset` không thể tự nó chứng minh rằng rollup luôn đầy đủ. Row cũ vẫn ghi vào raw
hypertable và tạo invalidation, nhưng refresh policy chỉ xử lý cửa sổ của lần chạy; vùng cũ giữ
materialization trước đó cho tới khi có explicit refresh. Không exception nào báo con số đang thiếu.

## Decision

Dùng ba lớp, mỗi lớp trả lời một loại tình huống:

1. Policy thường xuyên dùng `start_offset = 5 giờ`, tính bằng **2 giờ backlog + 2 giờ simulator
   clock skew + 1 giờ margin**; `end_offset = 1 phút`, chạy mỗi phút.
2. `make rollup-refresh-wide` refresh một khoảng đã đóng. Mặc định là đúng một cặp bảy ngày kết thúc
   trước một phút hoàn chỉnh; input phải minute-aligned, `from < to`, `from` còn trong raw horizon
   399 ngày, và một lần không quá 399 ngày.
   M13 mới gắn lịch chạy thưa; C07 chỉ tạo recovery operation có thể chạy lại.
3. D5 đối chiếu `sum(sample_count)` với raw `count(*)` trên cùng khoảng đã đóng. Đây là lớp duy nhất
   phát hiện cả giả định vận hành sai lẫn wide refresh không chạy.

Rollup đặt `timescaledb.materialized_only = true` và tạo `WITH NO DATA`. Query vì thế đọc đúng sản
phẩm đã materialize, không nối raw tail để che bucket cũ; migration cũng không tự backfill một khối
lượng không biết trước. CAGG giữ 15 năm, khác raw telemetry giữ 400 ngày, vì auditor hỏi lịch sử ở
mức một phút trong khi kỹ sư chỉ cần điểm thô cho điều tra gần đây.

Điều kiện phải tính lại `5 giờ`: thay operating outage budget, simulator clock-skew envelope, dung
lượng/tốc độ xả buffer, hoặc tần suất refresh. Nếu cần một bảo đảm cứng theo thời gian thì gateway
phải có age policy rõ ràng; chỉ tăng `start_offset` không biến byte cap thành time cap.

## Alternatives considered

### Chỉ tăng policy lên 7 ngày

Mỗi phút trả lại chi phí quét một sự cố hiếm, vẫn không bao phủ outage dài hơn 7 ngày, và không có
phép đếm phát hiện giả định sai.

### Bật real-time aggregation

Nó nối phần raw mới hơn watermark, không sửa materialization cũ nằm dưới watermark. Tệ hơn, query có
thể trông đúng ở phần đuôi trong khi reconciliation của sản phẩm lưu trữ vẫn chưa chạy.

### Không dùng rollup

Truy vấn trực tiếp raw tránh consistency gap nhưng chuyển toàn bộ chi phí sang mỗi lần đọc và không
đạt D2. Raw vẫn được giữ làm nguồn đối chứng; nó không phải đường đọc dài hạn mặc định.

## Consequences

**Được**

- Đường đọc nói thật nó đang đọc dữ liệu đã materialize hay raw.
- Trường hợp phổ biến được sửa trong tối đa khoảng một phút sau khi row nằm trong cửa sổ 5 giờ.
- Phần đuôi không giới hạn của byte-capped buffer có recovery path và một oracle định lượng.
- Retention 15 năm của rollup độc lập với retention 400 ngày của raw.

**Mất / phải chịu**

- Từ lúc row rất muộn tới wide refresh, rollup có chủ ý là stale; consumer phải biết D5 là health
  signal chứ không phải log trang trí.
- Wide refresh không cứu được raw chunk đã bị retention xoá. Gate tuổi 399 ngày còn ngăn một refresh
  lịch sử tính lại rollup 15 năm từ nguồn raw đã rỗng; nó không làm dữ liệu sống lại.
- M3 chưa tự động hoá lịch chạy thưa. M13 nhận operation đã kiểm chứng và phải chọn lịch bằng số đo.

## Verification

- Integration test xác nhận CAGG dựng trực tiếp trên `ts.telemetry_measurement`, chỉ nhận `real`,
  `materialized_only = true`, hai policy đúng `5 giờ / 1 phút / 1 phút` và `15 năm`.
- Test boundary ghi mẫu ở `59,999` giây, refresh khoảng `[10:00, 10:02)`, rồi chứng minh bucket đầu
  có đúng 2 mẫu, bucket sau đúng 1 mẫu, và hai site không nhập vào nhau.
- `make rollup-refresh-wide` in đúng cặp `from/to` cùng số giây đã refresh; input mở, đảo chiều,
  không minute-aligned, cũ hơn raw horizon hoặc dài hơn 399 ngày đều dừng trước `CALL`.
- C11 bổ sung số đo reconciliation trước/sau wide refresh. ADR này chưa coi D5 là đạt trước bằng
  chứng đó.
