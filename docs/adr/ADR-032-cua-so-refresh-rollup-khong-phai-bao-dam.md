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

1. Policy thường xuyên của rollup mức kênh dùng `start_offset = 5 giờ`, tính bằng **2 giờ backlog +
   2 giờ simulator clock skew + 1 giờ margin**; `end_offset = 1 phút`, chạy mỗi phút. Rollup mức máy
   đọc từ rollup mức kênh nên dùng cùng `start_offset`, `end_offset = 2 phút` và cũng chạy mỗi phút.
2. `make rollup-refresh-wide` refresh một khoảng đã đóng theo đúng thứ tự phụ thuộc: rollup mức kênh
   (**parent**) trước, rollup mức máy (**child**) sau, cùng một cặp `from/to`. Mặc định là đúng một cặp
   bảy ngày kết thúc trước một phút hoàn chỉnh; input phải minute-aligned, `from < to`, `from` còn
   trong raw horizon 399 ngày, và một lần không quá 399 ngày.
   M13 mới gắn lịch chạy thưa; C07 chỉ tạo recovery operation có thể chạy lại.
3. D5 đối chiếu ba tầng trên cùng khoảng đã đóng: raw `count(*)` ↔ parent `sum(sample_count)` ↔ child
   `sum(sample_count)`. Đây là lớp duy nhất phát hiện cả giả định vận hành sai, wide refresh không chạy,
   hoặc orchestration dừng sau parent trong khi dashboard mức máy vẫn đọc child cũ.

Rollup đặt `timescaledb.materialized_only = true` và tạo `WITH NO DATA`. Query vì thế đọc đúng sản
phẩm đã materialize, không nối raw tail để che bucket cũ; migration cũng không tự backfill một khối
lượng không biết trước. Chân trời của CAGG là 15 năm, khác raw telemetry 400 ngày, vì auditor hỏi
lịch sử ở mức một phút trong khi kỹ sư chỉ cần điểm thô cho điều tra gần đây.

**Cả hai chân trời hiện là chính sách, không phải job đang chạy.** Migration `007` gỡ retention của
raw và `010` gỡ retention của rollup, cho tới khi có legal hold ở **M12** — `scope.md` §8.4 nói cờ
`legal_hold` chặn **mọi** retention policy, và "mọi" không chừa rollup ra. Lập luận *"rollup là dữ
liệu dẫn xuất nên xoá được"* sai ngay trong chính ADR này: sau ngày 400 raw đã bị xoá, nên rollup
**không dẫn xuất từ gì nữa** — nó là bản ghi duy nhất còn lại của quãng đó, và job retention trên nó
lặng lẽ là mắt xoá cuối cùng của cả chuỗi.

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
- Chân trời 15 năm của rollup độc lập với chân trời 400 ngày của raw — nhưng **không** độc lập với
  legal hold: cả hai job đều đang tắt tới M12 (`007`, `010`).

**Mất / phải chịu**

- Từ lúc row rất muộn tới wide refresh, rollup có chủ ý là stale; consumer phải biết D5 là health
  signal chứ không phải log trang trí.
- Wide refresh không cứu được raw chunk đã bị xoá. Gate tuổi 399 ngày còn ngăn một refresh lịch sử
  tính lại rollup từ nguồn raw đã rỗng; nó không làm dữ liệu sống lại. Tới M12 thì không có gì bị xoá
  tự động cả, nên gate này hiện phòng cho `drop_chunks` chạy tay.
- M3 chưa tự động hoá lịch chạy thưa. M13 nhận operation đã kiểm chứng và phải chọn lịch bằng số đo.

## Verification

- Integration test xác nhận CAGG dựng trực tiếp trên `ts.telemetry_measurement`, chỉ nhận `real`,
  `materialized_only = true`, và policy refresh đúng `5 giờ / 1 phút / 1 phút`. Policy retention
  `15 năm` đã bị `010` gỡ; test khẳng định **không** còn `policy_retention` nào trên toàn schema `ts`
  — phát biểu theo hình dạng của luật, không theo từng bảng, để bảng tiếp theo được phủ sẵn.
- Test boundary ghi mẫu ở `59,999` giây, refresh khoảng `[10:00, 10:02)`, rồi chứng minh bucket đầu
  có đúng 2 mẫu, bucket sau đúng 1 mẫu, và hai site không nhập vào nhau.
- `make rollup-refresh-wide` in đúng cặp `from/to`, số giây, số aggregate và thứ tự refresh; input mở,
  đảo chiều, không minute-aligned, cũ hơn raw horizon hoặc dài hơn 399 ngày đều dừng trước `CALL`.
- C11 ngày 2026-09-01 trên stack thật tạo một máy riêng cho mỗi lượt và đo đúng chuỗi
  raw/parent/child: `0/0/0 → 3/0/0 → 3/0/0 → 3/3/0 → 3/3/3`. Hai negative control đều trả
  `P1101`: policy thường xuyên bỏ sót row cũ, và refresh chỉ parent vẫn để child thiếu **3** mẫu.
- Cùng bucket `[2026-09-01T10:05:00Z, 10:06:00Z)`, `make rollup-refresh-wide` in
  `refreshed_seconds|60|refreshed_aggregates|2|order|parent_then_machine`. D5 vì thế phủ đủ chuỗi
  phân cấp; chi tiết số đo nằm trong `docs/benchmarks.md`.
