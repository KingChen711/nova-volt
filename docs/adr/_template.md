# ADR-NNN — <Quyết định, viết ở thể khẳng định>

| | |
|---|---|
| **Status** | Proposed · **Accepted** · Superseded by ADR-NNN · Deprecated |
| **Date** | YYYY-MM-DD |
| **Liên quan** | ADR-NNN, `docs/scope.md` §X, `docs/plans/MN-*.md` §Y |

---

## Context

Chuyện gì buộc phải quyết? Viết **tình huống**, không viết giải pháp.

Ba thứ phải có mặt, nếu không thì ba tháng sau đọc lại sẽ không hiểu:

1. **Ràng buộc thật** — số đo, phiên bản, giới hạn phần cứng, yêu cầu nghiệp vụ cụ thể. Không
   viết "cần hiệu năng tốt"; viết "p95 < 200 ms trên 100k cell".
2. **Cái gì sẽ hỏng nếu không quyết** — quyết định nào đang bị chặn.
3. **Điều đã biết lúc đó**, kể cả thứ sau này hoá ra sai. ADR là ảnh chụp thời điểm, không phải
   tài liệu sống.

## Decision

Một đoạn, thể chủ động, hiện tại: *"Dùng X cho Y."* Không "sẽ", không "nên".

Nếu quyết định có phạm vi hẹp thì nói rõ ranh giới — cái gì **không** thuộc quyết định này.

## Consequences

Chia hai chiều, và **chiều xấu phải dài hơn hoặc bằng chiều tốt**. ADR chỉ toàn ưu điểm là ADR
chưa nghĩ xong.

**Được**

- …

**Mất / phải chịu**

- …

**Việc phát sinh** — thứ giờ bắt buộc phải làm vì đã quyết như vậy:

- …

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| … | … |

Phương án bị loại **vì lý do sai** cũng ghi lại. Khi lý do đó không còn đúng nữa, đây là chỗ
người ta tìm để mở lại quyết định.

## Evidence

Số đo, output lệnh, hoặc trích code — thứ **chạy lại được**. Không dùng ảnh chụp màn hình
(`docs/plans/M0-bootstrap.md` §C13).

Nếu quyết định dựa trên phán đoán chứ không phải phép đo, **nói thẳng ra** ở đây. Đó là thông
tin, không phải điểm yếu.
