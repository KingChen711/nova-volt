# ADR-029 — Rate limit khi flush, và vì sao gateway phải chậm lại thay vì thử lại nhanh hơn

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-28 |
| **Liên quan** | `ADR-027` (HTTP protobuf gateway → ingestion), `ADR-028` (buffer append-only), `ADR-013` (retry + jitter của bus), [`scope.md`](../scope.md) §9/M2 lab #3, [`plans/M2-simulator-ingestion-idempotency.md`](../plans/M2-simulator-ingestion-idempotency.md) §C10 |

---

## Context

`ADR-028` cho gateway giữ dữ liệu khi ingestion chết. Nó **không** nói gì về lúc ingestion sống lại,
và đó mới là lúc nguy hiểm.

Ba sự thật cùng lúc:

1. Mạng OT không chớp cho **một** gateway. Cả khu `FORMATION` mất mạng cùng lúc và nối lại cùng lúc.
2. Buffer 30 phút ở 5.000 msg/s là ~9 triệu message trên đĩa của **mỗi** gateway.
3. Thời điểm mọi gateway nối lại là thời điểm ingestion vừa khởi động — pool connection lạnh, cache
   trống, và bảng dedup của `ADR-030` là một global index đang phải nhận write.

Phản xạ "xả hết thật nhanh cho an toàn" biến ba điều đó thành một cuộc tấn công tự gây ra. Ingestion
sập lần hai, gateway thấy lỗi nên retry dày hơn, và vòng lặp tự siết — **retry storm**. Sự cố mạng 30
giây thành sự cố hệ thống nửa tiếng, và nó xảy ra **sau** khi phần khó (không mất dữ liệu) đã xong.

Ingestion cũng không có cách nào nói "chậm lại" nếu nó chỉ biết nhận hoặc chết. Không có tín hiệu thì
gateway không có gì để nghe, và mọi thứ ở phía gateway chỉ là đoán.

## Decision

Hai cái phanh độc lập, ở hai đầu, vì hai lý do khác nhau.

**Phía ingestion — nói ra rằng mình quá tải.**
Concurrency limiter trên đúng endpoint batch: `MaxConcurrentBatches` transaction đồng thời,
`MaxQueuedBatches` chờ, quá thì trả **`429`** kèm **`Retry-After`**. `429` chứ không phải `503`: dịch
vụ vẫn khoẻ và request vẫn hợp lệ — chỉ là quá nhiều cùng lúc. Health check nằm **ngoài** limiter,
nếu không Docker sẽ restart một container chỉ vì nó bận, và backpressure thành outage.

**Phía gateway — tự giới hạn kể cả khi không ai kêu.**
Token bucket trên số message mỗi giây (`FlushMessagesPerSecond`, mặc định **12.000**, burst một
giây). Con số này cao hơn N1 (5.000 msg/s) đủ để tiêu backlog trong ngân sách 3 phút của D3, và
không cao tới mức xả sạch buffer trong một nhịp.

Khi POST hỏng: exponential backoff `FlushRetryDelay` → `FlushRetryMaxDelay`, **cộng jitter**.
`Retry-After` của ingestion là **sàn**, không phải gợi ý — nếu nó lớn hơn backoff của ta thì ta chờ
theo nó.

**Một điểm khác `NvmRetryPolicy` của M1**: jitter ở đây **chỉ nới dài, không rút ngắn**. `NvmRetryPolicy`
dùng jitter đối xứng vì ở đó không có ai đưa ra sàn. Ở đây có, và jitter đối xứng sẽ cho gateway quay
lại **sớm hơn** đúng cái mốc server vừa nói là nó chịu được. Nới một phía vẫn phá được sự đồng pha —
thứ duy nhất jitter cần làm.

Metric: `gateway.flush.rate`, `gateway.flush.throttled` (server bảo chậm), `gateway.flush.rate_limited`
(ta tự chậm). Ba cái, không phải hai: một đường depth đi xuống chậm có hai nguyên nhân hoàn toàn khác
nhau, và lab #3 tồn tại để phân biệt đúng hai nguyên nhân đó.

## Consequences

**Được**

- Backlog tiêu **đều** thay vì tiêu theo xung. Ingestion thấy tải phẳng, không thấy tường.
- Gateway có một hành vi xác định khi ingestion kêu, thay vì phụ thuộc vào timeout của `HttpClient`.
- Số throttle đọc được, nên "vì sao chậm" trả lời được bằng số chứ không bằng phỏng đoán.

**Mất / phải chịu**

- Rate limit là một **con số đoán trước**. 12.000 msg/s đúng cho một gateway ở N1; mười gateway cùng
  xả vẫn có thể vượt sức ingestion, và tầng bảo vệ còn lại lúc đó chỉ là `429`. M13 mới có số liệu
  nhiều site để chỉnh.
- Backlog lớn tiêu **lâu hơn** so với xả không giới hạn. Đây là đánh đổi có chủ ý, và lab #3 phải đo
  xem cái giá đó mua được gì.
- Thêm một tham số vận hành phải hiểu mới chỉnh đúng. Đặt bằng 0 là tắt hẳn — có ích cho lab, nguy
  hiểm cho production.

**Giới hạn còn để ngỏ**

- Jitter tính một lần cho mỗi lần retry của **một** process. Nó tách được n gateway khỏi nhau, không
  tách được n batch trong cùng một gateway — nhưng gateway chỉ có một batch outstanding một lúc, nên
  giới hạn này không chạm tới đường đi thật.
- Không có adaptive rate: gateway không tự nâng tốc khi thấy ingestion rảnh. Cần số liệu của C16
  trước khi thêm một vòng điều khiển nữa.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Không rate limit, chỉ dựa vào `429` | Ingestion phải **bị đau trước** thì mới có ai chậm lại. Với dedup key là global index, "đau" có thể là lock contention chứ không phải 429 kịp thời |
| Chỉ rate limit, không nghe `Retry-After` | Một con số cố định không biết ingestion đang gánh mấy gateway. Server là bên duy nhất biết tổng tải |
| Sleep cố định giữa các batch | Gateway rảnh cũng bị phạt, và tốc độ thật phụ thuộc kích thước batch — không phát biểu được thành một con số msg/s |
| `System.Threading.RateLimiting` phía gateway | Nó tính theo **request**, còn ngân sách thật đo bằng **message**. Một batch có thể mang 1 hay 10.000 message |
| Circuit breaker thay vì backoff | Breaker mở nghĩa là ngừng thử — đúng cho lỗi vĩnh viễn, sai cho quá tải, vì quá tải tự hết khi ta chậm lại |

## Evidence

Unit test (`FlushPacingTests`, `StoreAndForwardFlusherTests`) khoá phần logic:

- token bucket tiêu burst rồi mới pace; batch lớn hơn burst **vẫn qua** (nếu không, cursor kẹt vĩnh
  viễn và buffer lớn dần tới ngưỡng đĩa);
- backoff nhân đôi tới trần và **không tràn** sau nhiều giờ hỏng;
- 200 lần rút jitter với `Retry-After = 4 s` đều nằm trong `[4 s, 5 s]` — **không lần nào** dưới sàn;
- `Retry-After` nhỏ hơn backoff của ta **không** làm ta nhanh lên.

Integration test (`IngestionAdmissionControlTests`) khoá phần hợp đồng phía server: ingestion bão hoà
trả `429` + `Retry-After: 3`, và `/health/live` **vẫn 200** trong lúc đó.

**Lab §5.C10.2 (D3) và lab #3 (`scope.md` §9/M2) — chưa chạy tại thời điểm commit C10.**
Bốn con số của mỗi lab điền vào `benchmarks.md` và quay lại mục này ở C19. Cho tới lúc đó, quyết định
này **chưa có bằng chứng ở quy mô thật** — và theo `AGENTS.md` §3.2, nếu lần A (tắt rate limit) cho
thấy không có vấn đề gì, điều đó phải được ghi thẳng vào đây thay vì giữ rate limit vì nó là một
pattern nổi tiếng.
